import { Injectable, inject } from '@angular/core';
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import { resolveApiBase } from './api-base';
import { ConnectionStatusDto, MonitoredNodeDto, NodeReadingDto, SnapshotDto } from './api.types';
import { MonitorStore } from './monitor-store';

/**
 * Backoff for the browser's own socket, in milliseconds.
 *
 * Front-loaded: an API restart during development, or a laptop waking up, is
 * back within a second or two, and the common case deserves a fast retry. The
 * tail is capped at ten seconds because unlike the service's OPC reconnect there
 * is a human watching this one, and a dashboard that takes half a minute to
 * notice the server came back reads as broken.
 */
const RETRY_DELAYS_MS = [0, 1_000, 2_000, 5_000, 10_000];

/**
 * Owns the SignalR connection and translates hub messages into store updates.
 *
 * Nothing else in the app talks to SignalR, and this class holds no state of its
 * own beyond the connection — which keeps "what does the server push" answerable
 * by reading one file.
 */
@Injectable({ providedIn: 'root' })
export class HubClient {
  private readonly store = inject(MonitorStore);
  private connection: HubConnection | null = null;

  get hubUrl(): string {
    return `${resolveApiBase()}/hubs/monitoring`;
  }

  async start(): Promise<void> {
    if (this.connection) return;

    const connection = new HubConnectionBuilder()
      .withUrl(this.hubUrl)
      .withAutomaticReconnect(RETRY_DELAYS_MS)
      .configureLogging(LogLevel.Warning)
      .build();

    // Server-to-client messages, matching IMonitoringClient on the API side.
    connection.on('SnapshotReceived', (snapshot: SnapshotDto) => this.store.applySnapshot(snapshot));
    connection.on('ReadingsUpdated', (readings: NodeReadingDto[]) =>
      this.store.applyReadings(readings),
    );
    connection.on('ConnectionStateChanged', (status: ConnectionStatusDto) =>
      this.store.applyConnection(status),
    );
    connection.on('NodesChanged', (nodes: MonitoredNodeDto[]) => this.store.applyNodes(nodes));

    connection.onreconnecting(() => this.store.setLink('reconnecting'));
    connection.onclose(() => {
      this.store.setLink('disconnected');
      // withAutomaticReconnect gives up after the last delay. Beyond that the
      // dashboard retries on its own, because a monitor that stays dead after
      // the server is back is worse than one that keeps knocking.
      void this.retryForever();
    });

    connection.onreconnected(() => {
      this.store.setLink('connected');
      // The hub pushes a snapshot on a *new* connection, not on a resumed one,
      // and whatever happened during the gap was not queued anywhere. Asking
      // explicitly is the only way to be sure the dashboard is not showing
      // pre-outage values as if they were current.
      void this.resync();
    });

    this.connection = connection;
    await this.retryForever();
  }

  /** Pulls a fresh snapshot. Also used by the UI after subscribing to a node. */
  async resync(): Promise<void> {
    if (this.connection?.state !== HubConnectionState.Connected) return;

    try {
      const snapshot = await this.connection.invoke<SnapshotDto>('GetSnapshot');
      this.store.applySnapshot(snapshot);
    } catch {
      // The socket dropped mid-call. The reconnect path will resync.
    }
  }

  async stop(): Promise<void> {
    const connection = this.connection;
    this.connection = null;
    await connection?.stop();
  }

  private async retryForever(): Promise<void> {
    let attempt = 0;

    while (this.connection && this.connection.state === HubConnectionState.Disconnected) {
      try {
        await this.connection.start();
        this.store.setLink('connected');
        return;
      } catch {
        attempt++;
        this.store.setLink('reconnecting');
        const delay = RETRY_DELAYS_MS[Math.min(attempt, RETRY_DELAYS_MS.length - 1)];
        await new Promise((resolve) => setTimeout(resolve, Math.max(delay, 1_000)));
      }
    }
  }
}
