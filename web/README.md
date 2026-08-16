# web — the dashboard

Angular 20, standalone components, signals, zoneless change detection. Three
runtime dependencies: Angular, `@microsoft/signalr` and `rxjs`. No chart library
— the trend plots are hand-drawn SVG in `components/trend-chart.ts`.

```bash
npm ci
npm start     # http://localhost:4200, expects the API on http://localhost:8080
npm run build # production bundle into dist/
```

The API must be running; see the repository README. The dev server and the API
are different origins, and the API's CORS policy allows `localhost:4200` and
`127.0.0.1:4200` by default.

## Layout

```
src/app/core/api.types.ts     mirrors of the DTOs in src/OpcMonitor.Api/Contracts.cs
src/app/core/api-base.ts      where the API is, resolved at runtime not at build time
src/app/core/hub-client.ts    the SignalR connection — the only file that imports it
src/app/core/monitor-api.ts   the REST calls: browse, subscribe, unsubscribe
src/app/core/monitor-store.ts all mutable state, as signals; one method per hub message
src/app/components/           status bar, browse tree, node card, node detail, chart
```

State lives only in `MonitorStore`. Components read signals and render; every
write goes through an `apply*` method that corresponds one-to-one with a hub
message, so "what can change this screen" is answerable by reading one file.
