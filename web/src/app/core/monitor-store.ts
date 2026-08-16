import { Injectable, computed, signal } from '@angular/core';
import {
  ConnectionStatusDto,
  MonitoredNodeDto,
  NodeReadingDto,
  NodeStatusDto,
  QualitySeverity,
  SnapshotDto,
} from './api.types';
import { asNumber } from './format';

/** One point on a node's chart. */
export interface Sample {
  /** Epoch milliseconds of the reading's effective timestamp. */
  t: number;
  /** Numeric value, or null for a node whose values are not numbers. */
  v: number | null;
  severity: QualitySeverity;
}

/** Everything the dashboard knows about one node. */
export interface NodeView {
  node: MonitoredNodeDto;
  current: NodeReadingDto | null;
  withinBand: boolean | null;
  samples: Sample[];
  /** True once a numeric value has been seen, which is what makes a chart meaningful. */
  numeric: boolean;
  /** Readings received since the page loaded. Makes a stalled node obvious. */
  updates: number;
}

/** State of the browser's own socket to the API, distinct from the OPC session. */
export type LinkState = 'connected' | 'reconnecting' | 'disconnected';

/**
 * What the operator is actually being told, once both links in the chain are
 * taken into account.
 */
export type OverallState = 'connected' | 'reconnecting' | 'degraded' | 'offline';

/**
 * Points retained per node.
 *
 * The service's own window is 120 readings; keeping rather more than that means
 * a chart continues to fill in after the snapshot instead of being permanently
 * capped at whatever the server happened to be holding.
 */
const MAX_SAMPLES = 400;

/** Sliding window the update-rate readout is computed over. */
const RATE_WINDOW_MS = 5_000;

/**
 * How long a node may go without reporting before it is called quiet.
 *
 * "Quiet", not "stale", and the distinction is the point. A data-change
 * subscription reports changes, so a tag that genuinely has not moved reports
 * nothing — a service level pinned at 255 for an hour is a healthy tag, not a
 * broken one. What the operator needs is the fact, stated plainly, so they can
 * apply their own judgement: a constant is fine, a flow rate that has not
 * twitched in two minutes is not. Calling it "stale" would be the dashboard
 * asserting a fault it is not in a position to diagnose.
 */
export const QUIET_AFTER_MS = 120_000;

/**
 * The dashboard's model of the plant, assembled from the snapshot and kept
 * current by hub messages.
 *
 * Deliberately the only mutable state in the app: components read signals and
 * render, and every write goes through one of the `apply*` methods below, which
 * correspond one-to-one with the hub messages the server sends.
 */
@Injectable({ providedIn: 'root' })
export class MonitorStore {
  private readonly _nodes = signal<NodeView[]>([]);
  private readonly _connection = signal<ConnectionStatusDto | null>(null);
  private readonly _link = signal<LinkState>('disconnected');
  private readonly _selectedId = signal<string | null>(null);
  private readonly _lastMessageAt = signal<number>(0);

  /** Receive times of recent readings, for the rate readout. Not rendered directly. */
  private readonly _recent = signal<number[]>([]);

  /** Ticks so ages, timers and the rate readout stay live between messages. */
  readonly now = signal<number>(Date.now());

  readonly nodes = this._nodes.asReadonly();
  readonly connection = this._connection.asReadonly();
  readonly link = this._link.asReadonly();
  readonly selectedId = this._selectedId.asReadonly();
  readonly lastMessageAt = this._lastMessageAt.asReadonly();

  readonly selected = computed<NodeView | null>(() => {
    const id = this._selectedId();
    const nodes = this._nodes();
    if (nodes.length === 0) return null;
    return nodes.find((n) => n.node.id === id) ?? nodes[0];
  });

  /**
   * The single honest answer to "is this working", combining the two independent
   * links: browser to API, and API to OPC server. Collapsing them would let a
   * dead dashboard socket look identical to a dead OPC session, and the
   * remedies for those are not the same.
   */
  readonly overall = computed<OverallState>(() => {
    const link = this._link();
    if (link !== 'connected') return 'offline';

    switch (this._connection()?.state) {
      case 'connected':
        return 'connected';
      case 'connecting':
      case 'reconnecting':
        return 'reconnecting';
      default:
        return 'degraded';
    }
  });

  /** Readings per second across all nodes, over the last few seconds. */
  readonly updateRate = computed<number>(() => {
    const cutoff = this.now() - RATE_WINDOW_MS;
    const count = this._recent().filter((t) => t >= cutoff).length;
    return count / (RATE_WINDOW_MS / 1000);
  });

  readonly nodeCount = computed(() => this._nodes().length);

  readonly quietCount = computed(() => {
    const now = this.now();
    return this._nodes().filter(
      (n) => n.current !== null && now - new Date(n.current.timestamp).getTime() > QUIET_AFTER_MS,
    ).length;
  });

  readonly badCount = computed(
    () => this._nodes().filter((n) => n.current?.qualitySeverity === 'bad').length,
  );

  readonly alarmCount = computed(() => this._nodes().filter((n) => n.withinBand === false).length);

  tick(): void {
    this.now.set(Date.now());
  }

  select(id: string): void {
    this._selectedId.set(id);
  }

  setLink(state: LinkState): void {
    this._link.set(state);
  }

  /**
   * Replaces everything. Sent by the hub on connect and requested again after a
   * socket reconnect, when the dashboard has no idea what it missed.
   */
  applySnapshot(snapshot: SnapshotDto): void {
    this._connection.set(snapshot.connection);
    this._nodes.set(sorted(snapshot.nodes.map((status) => toView(status))));
    this._lastMessageAt.set(Date.now());

    if (this._selectedId() === null && snapshot.nodes.length > 0) {
      this._selectedId.set(mostInteresting(this._nodes()).node.id);
    }
  }

  applyConnection(status: ConnectionStatusDto): void {
    this._connection.set(status);
    this._lastMessageAt.set(Date.now());
  }

  /**
   * Reconciles the node list against the server's.
   *
   * Nodes are matched by id and their samples carried across, because this
   * message also fires on reconnect, when nothing about the nodes themselves has
   * changed — discarding the series there would blank every chart each time the
   * link hiccuped, which is precisely when the history is worth having.
   */
  applyNodes(nodes: MonitoredNodeDto[]): void {
    const existing = new Map(this._nodes().map((view) => [view.node.id, view]));

    this._nodes.set(
      sorted(
        nodes.map((node) => {
          const previous = existing.get(node.id);
          return previous
            ? { ...previous, node }
            : { node, current: null, withinBand: null, samples: [], numeric: false, updates: 0 };
        }),
      ),
    );

    this._lastMessageAt.set(Date.now());
  }

  /** A batch of value changes, as delivered by `ReadingsUpdated`. */
  applyReadings(readings: NodeReadingDto[]): void {
    if (readings.length === 0) return;

    const byNode = new Map<string, NodeReadingDto[]>();
    for (const reading of readings) {
      const list = byNode.get(reading.nodeId);
      if (list) list.push(reading);
      else byNode.set(reading.nodeId, [reading]);
    }

    this._nodes.update((views) =>
      views.map((view) => {
        const batch = byNode.get(view.node.id);
        return batch ? append(view, batch) : view;
      }),
    );

    const receivedAt = Date.now();
    this._recent.update((times) =>
      [...times, ...readings.map(() => receivedAt)].filter((t) => t >= receivedAt - RATE_WINDOW_MS),
    );
    this._lastMessageAt.set(receivedAt);
  }
}

/**
 * One ordering for the grid, applied to every message that can change the node
 * set.
 *
 * The two messages that carry a node list do not agree: the snapshot arrives
 * sorted by display name, while `NodesChanged` arrives in the order the server
 * resolved the nodes in. Taking each at face value made every card jump to a new
 * position on reconnect — the exact moment an operator is looking hardest at the
 * screen and least able to afford the layout moving underneath them. Sorting
 * here rather than trusting either message means the grid is stable by
 * construction.
 */
function sorted(views: NodeView[]): NodeView[] {
  return [...views].sort((a, b) =>
    a.node.displayName.localeCompare(b.node.displayName, 'en', { sensitivity: 'base' }),
  );
}

/**
 * Which node the detail pane opens on.
 *
 * The largest panel on the page should not open on a signal with nothing to
 * show. A timestamp or an enum has no trend at all, and a tag pinned at a
 * constant — a service level at 255, say — has a trend that is a horizontal
 * line. Both are perfectly healthy and both make the dashboard look broken on
 * first paint. So: prefer numeric, and among numeric prefer the one that is
 * actually moving, measured as spread relative to magnitude so that a pressure
 * in bar and a level in metres are compared fairly.
 */
function mostInteresting(views: NodeView[]): NodeView {
  const scored = views
    .filter((view) => view.numeric)
    .map((view) => ({ view, score: variation(view) }))
    .sort((a, b) => b.score - a.score);

  return scored[0]?.view ?? views[0];
}

function variation(view: NodeView): number {
  const values = view.samples.map((s) => s.v).filter((v): v is number => v !== null);
  if (values.length < 2) return 0;

  const low = Math.min(...values);
  const high = Math.max(...values);
  const scale = Math.max(Math.abs(low), Math.abs(high), Number.EPSILON);

  return (high - low) / scale;
}

function toView(status: NodeStatusDto): NodeView {
  const samples = status.window.map(toSample);

  return {
    node: status.node,
    current: status.current ?? null,
    withinBand: status.withinBand ?? null,
    samples: samples.slice(-MAX_SAMPLES),
    numeric: samples.some((s) => s.v !== null),
    updates: 0,
  };
}

function append(view: NodeView, batch: NodeReadingDto[]): NodeView {
  const last = batch[batch.length - 1];
  const samples = [...view.samples, ...batch.map(toSample)].slice(-MAX_SAMPLES);

  return {
    ...view,
    current: last,
    withinBand: classify(view.node, last),
    samples,
    numeric: view.numeric || samples.some((s) => s.v !== null),
    updates: view.updates + batch.length,
  };
}

/**
 * Band classification is recomputed in the browser for readings that arrive over
 * the hub, because `ReadingsUpdated` carries the reading alone — the band belongs
 * to the node, not to the reading, and sending it with every value would be
 * repeating a constant thousands of times a minute.
 */
function classify(node: MonitoredNodeDto, reading: NodeReadingDto): boolean | null {
  if (node.minimum === undefined && node.maximum === undefined) return null;

  const value = asNumber(reading.value);
  if (value === null) return null;

  if (node.minimum !== undefined && value < node.minimum) return false;
  if (node.maximum !== undefined && value > node.maximum) return false;
  return true;
}

function toSample(reading: NodeReadingDto): Sample {
  return {
    t: new Date(reading.timestamp).getTime(),
    v: asNumber(reading.value),
    severity: reading.qualitySeverity,
  };
}
