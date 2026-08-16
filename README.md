# opc-ua-monitor

Real-time monitoring for industrial OPC UA servers. A .NET 8 service subscribes
to nodes on an OPC UA server and streams value changes over SignalR to an Angular
dashboard that updates live. It handles the parts that make OPC UA awkward in
practice: application certificate trust, endpoint advertisement inside
containers, session reconnection with automatic resubscription, and data-change
subscriptions instead of polling.

![Live values updating](docs/live.gif)

![The dashboard](docs/dashboard.png)

---

## Quickstart — no Docker, against a public server

Two terminals, and it runs against a public OPC UA demo server on the internet,
so there is nothing to install beyond the two SDKs.

```bash
git clone https://github.com/<you>/opc-ua-monitor
cd opc-ua-monitor

# terminal 1 — the service
dotnet run --project src/OpcMonitor.Api --environment Remote

# terminal 2 — the dashboard
cd web && npm ci && npm start
```

Then open <http://localhost:4200>.

Requires the .NET 8 SDK and Node 20+. The `Remote` profile talks to
`opc.tcp://opcuademo.sterfive.com:26543`, so it needs outbound TCP 26543 — and
because it is somebody else's server, it can be down or busy.

The API alone is usable without the dashboard:

```bash
curl http://localhost:8080/health          # OPC session state, not just liveness
curl http://localhost:8080/api/nodes       # every monitored node, current value + window
curl http://localhost:8080/api/browse      # walk the server's address space
```

### Docker Compose — not yet verified

`docker-compose.yml` runs the OPC Foundation reference server, the API and a
network to join them, and is the intended one-command path. **It has not been
run end to end yet**, because no Docker daemon was available on the machine this
was built on. It is left in the repository as the design rather than as a
promise, and this note stays until someone has actually watched
`docker compose up` produce a working dashboard. Use the two-terminal quickstart
above, which is verified.

---

## The dashboard

Dense on purpose, and dark because it is meant to sit on a screen someone
glances at rather than reads.

The OPC session dropped — attempt, elapsed time, the server's own error and a
live countdown to the next retry:

![Reconnecting, with the backoff visible](docs/reconnecting.png)

The dashboard's own socket dropped, which is a different failure with a
different remedy. The values are held rather than blanked, and labelled as held:

![Dashboard link lost](docs/link-lost.png)

- **Live value per node**, with its engineering unit, its source timestamp to the
  millisecond, and how long ago that was. Values are monospaced and
  tabular-figured so digits do not jitter as they change, and the type size steps
  down for long values rather than clipping them.
- **A trend chart per node**, plus a larger one for the selected node. Charts
  autoscale to the data, shade the configured band behind it, and break the line
  across gaps instead of drawing through them.
- **Two connection states, both stated.** The browser's socket to the API and the
  API's session to the OPC server are separate things that fail separately, so
  the header shows both. During a reconnect the attempt number, the elapsed
  time, the server's own error text and a countdown to the next retry are all on
  screen — the backoff is the interesting part and it is not hidden behind a
  spinner.
- **Address-space browsing**, one level at a time, with each level's current
  values read as it is fetched. Press `+` on a variable to start monitoring it
  and `−` to stop; both take effect immediately and survive a reconnect.
- **Quiet, not stale.** A node that has not changed in two minutes is flagged as
  quiet rather than broken, because a data-change subscription reporting nothing
  about a constant tag is correct behaviour, not a fault.

---

## Architecture

```mermaid
flowchart LR
    S["OPC UA server<br/>(reference server)"]
    I["OpcMonitor.Infrastructure<br/>session · subscription · reconnect"]
    D["OpcMonitor.Domain<br/>readings · rolling window"]
    A["OpcMonitor.Api<br/>minimal API + SignalR hub"]
    W["Dashboard<br/>(browser)"]

    S -- "data change<br/>notifications" --> I
    I -- "readings" --> D
    D --> A
    A -- "SignalR" --> W
```

Data flows one way. `OpcMonitor.Domain` has no package references at all;
`OpcMonitor.Infrastructure` is the only project that knows OPC UA exists;
`OpcMonitor.Api` knows about HTTP and SignalR but nothing about the protocol;
`web` knows only the wire contract.

The hub pushes four messages, and the browser mirrors them in
`web/src/app/core/api.types.ts`:

| Message | Carries |
|---|---|
| `SnapshotReceived` | Everything: nodes, current values, windows, connection state. Sent on connect. |
| `ReadingsUpdated` | A batch of value changes, coalesced over a 100 ms window. |
| `ConnectionStateChanged` | OPC session state, reconnect attempt and the reason. |
| `NodesChanged` | The monitored node set, after a subscribe, unsubscribe or reconnect. |

---

## How it works

- **Subscriptions, not polling.** The server samples each node and pushes only
  changes, so traffic scales with how often values move rather than with how
  many nodes are watched. An optional absolute deadband suppresses noise on
  analogue signals server-side.

- **Reconnect with backoff, jitter and resubscribe.** When the session drops,
  the client rebuilds session, node resolution and subscription from scratch on
  an exponential backoff with symmetric jitter. One path, always exercised —
  rather than a subscription-transfer fast path plus a rebuild fallback that is
  the least-tested code in the system.

- **Current values on resubscribe.** A data-change subscription reports changes,
  so a node that is stable when you attach reports nothing until it happens to
  move. Every subscribe is followed by an explicit read of all monitored nodes,
  so the dashboard is populated within a second of connecting — on first start
  and after every reconnect.

- **Snapshot on connect.** The hub sends a browser its full state — nodes,
  current values, rolling window, connection status — the moment it connects, so
  the first paint is never empty.

- **The container endpoint trap, handled twice.** An OPC UA server advertises
  endpoint URLs built from its own hostname. In Compose that is the container
  id, which resolves nowhere. The compose file pins the simulator's `hostname:`
  to match, *and* the client keeps the host and port it actually connected to
  rather than the advertised one — because a server you do not control will not
  have had the first fix applied. See `EndpointUrlRewriter`.

- **Certificate trust is explicit.** A self-signed application instance
  certificate is generated on first run into a gitignored `pki/` directory.
  Accepting an untrusted server certificate is an opt-in flag, on in the compose
  profile, and every acceptance is logged as a warning with the certificate's
  subject and thumbprint.

- **Subscription lifetimes are computed, not guessed.** The lifetime count is
  raised to satisfy both the spec's three-keep-alive minimum and the session
  timeout, which is a common way a client appears to work and then quietly stops
  delivering.

- **No database.** An in-memory bounded window per node is enough for a live
  monitor, and an OPC UA server's own historical access does long-term storage
  better than a bolted-on table would.

- **Health means connected.** `/health` reports the OPC session state, so a
  container healthcheck on it is meaningful. Reconnecting is reported as
  degraded rather than unhealthy — restarting a container mid-backoff makes
  recovery slower, not faster.

- **Nodes can be added and dropped at runtime.** A node subscribed from the
  dashboard is attached to the existing subscription rather than triggering a
  rebuild, and it is held in a registry that the reconnect path resolves from —
  so it comes back after a dropped session instead of quietly vanishing. The
  registry is in memory only: a restart returns to the configured set, because a
  dashboard experiment should not become permanent state.

- **Browsing is lazy and reads as it goes.** `GET /api/browse` returns one level
  of the address space with the current value of every variable on it, in one
  batched read. Finding a tag is only half the problem; knowing which of six
  similar names is the live one is the other half.

---

## Pointing it at a different server

The endpoint is configuration. Three profiles ship with the API:

| Profile | Endpoint | Use |
|---|---|---|
| default (`appsettings.json`) | `opc.tcp://simulator:62541` | Docker Compose |
| `Local` | `opc.tcp://localhost:62541` | API on the host, simulator in Docker |
| `Remote` | `opc.tcp://opcuademo.sterfive.com:26543` | public demo server, no Docker at all |

```bash
dotnet run --project src/OpcMonitor.Api --environment Remote
```

Or override anything without touching a file:

```bash
Opc__EndpointUrl=opc.tcp://192.0.2.10:4840 dotnet run --project src/OpcMonitor.Api
```

The `Remote` profile talks to a public server on the internet: it needs outbound
TCP 26543, it can be down or busy, and it only offers an unsecured endpoint. It
is the profile everything here has actually been verified against.

The dashboard's own idea of where the API is follows the same principle. It is
read at runtime from one line of `index.html`:

```html
<meta name="opc-api-base" content="http://api.example.internal:8080" />
```

Left empty it uses the origin the page came from, which is the right answer when
a reverse proxy fronts both. Nothing about the endpoint is compiled into the
bundle either.

### Choosing which nodes to watch

Nodes are configured under `Opc:Nodes`, and each address is either a node id or
a browse path:

```json
{ "Address": "i=2258",                            "DisplayName": "Server time" }
{ "Address": "Server/ServerStatus/CurrentTime",   "DisplayName": "Server time" }
{ "Address": "Tank/TankLevel", "Unit": "m", "Minimum": 0, "Maximum": 1 }
```

Browse paths are resolved against the live server at startup. They are worth
preferring because namespace indices are assigned per server, so an
`ns=2;s=Something` copied from one server does not necessarily mean the same
thing on another. Nodes that do not resolve are skipped with a warning unless
marked `"Required": true`, so one config can list nodes for several servers.

The defaults use only nodes that OPC UA requires every compliant server to
expose, so the demo shows live data whatever you point it at.

---

## Finding out what a server actually exposes

`opcprobe` is a small command-line tool that connects, prints the endpoint and
security it negotiated, dumps the address space with live values, and can tail a
subscription. When the dashboard is blank, this tells you in ten seconds whether
the problem is OPC UA or everything downstream of it.

```bash
dotnet run --project tools/OpcMonitor.Probe -- \
  --endpoint opc.tcp://localhost:62541 --depth 3

dotnet run --project tools/OpcMonitor.Probe -- \
  --endpoint opc.tcp://localhost:62541 --watch 15
```

```
Connected.
  server        urn:example:SomeServer
  endpoint used opc.tcp://localhost:62541/
  security      SignAndEncrypt / Basic256Sha256
  namespaces    3

Address space:
  Server                       i=2253
    ServiceLevel                 i=2267    = 255
  Tank                         ns=1;i=1755
    TankLevel                    ns=1;i=1756 = 0.19186964722980
```

It uses the same session, resolver and subscription code as the service, so a
result here is evidence about the real pipeline rather than about a parallel
implementation written for the tool.

---

## Project layout

```
src/OpcMonitor.Domain           readings, quality, rolling window — zero package references
src/OpcMonitor.Infrastructure   the only project referencing the OPC UA SDK
src/OpcMonitor.Api              .NET 8 minimal API, SignalR hub, hosted service
web                             Angular 20 dashboard, standalone + signals, zoneless
tools/OpcMonitor.Probe          command-line diagnostic client
tests/OpcMonitor.Tests          domain, endpoint policy, reconnect policy, mapping
```

The dashboard has three dependencies in total: Angular, `@microsoft/signalr` and
`rxjs`. There is no chart library — the trend plots are hand-drawn SVG, which is
a smaller thing to own than a charting dependency used for exactly one chart
type, and it renders identically at every size the same geometry is reused at.

There is no `NuGet.config` in this repository, deliberately. Everything restores
from nuget.org, and CI proves it on a clean runner.

---

## Development

```bash
dotnet build -c Release
dotnet test

# run the API against the public demo server, no Docker needed
dotnet run --project src/OpcMonitor.Api --environment Remote

# the dashboard, with live reload
cd web && npm ci && npm start
```

Requires the .NET 8 SDK and Node 20+.

The dev server runs on port 4200 and the API on 8080, which are different
origins — the API's CORS policy allows both loopback spellings of `:4200` and
nothing else by default. Set `Cors:AllowedOrigins` to change that.

`pki/` is generated on first run and is gitignored. Application instance
certificates embed a hostname in their subject alternative names, and this one is
pinned to `localhost` in configuration precisely so that it is not the machine's:
`Opc:CertificateDomainNames` and `Opc:ApplicationUri` are both set explicitly
rather than left to the SDK, which fills them from `Dns.GetHostName()`.

CI runs on every push: a forbidden-strings scan over the working tree and the
full history, restore, build, test, an `npm ci && npm run build` of the
dashboard, and a `docker compose build`.

---

## License

MIT — see [LICENSE](LICENSE).

Built on the OPC Foundation's [UA-.NETStandard](https://github.com/OPCFoundation/UA-.NETStandard)
client SDK (`OPCFoundation.NetStandard.Opc.Ua.Client` and
`.Configuration`), used under its own license. The bundled simulator is the OPC
Foundation reference server container image.
