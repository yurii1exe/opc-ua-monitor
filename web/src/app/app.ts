import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { BrowsePanel } from './components/browse-panel';
import { NodeCard } from './components/node-card';
import { NodeDetail } from './components/node-detail';
import { StatusBar } from './components/status-bar';
import { HubClient } from './core/hub-client';
import { MonitorApi } from './core/monitor-api';
import { MonitorStore } from './core/monitor-store';

/**
 * How often ages, timers and the update-rate readout are recomputed.
 *
 * Independent of the data rate on purpose: a node that has stopped reporting
 * sends nothing, and its age is precisely the number that has to keep moving.
 * Four times a second is fast enough that the millisecond digits look live and
 * slow enough to be invisible on a CPU graph.
 */
const TICK_MS = 250;

@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StatusBar, BrowsePanel, NodeCard, NodeDetail],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnInit {
  readonly store = inject(MonitorStore);
  private readonly hub = inject(HubClient);
  private readonly api = inject(MonitorApi);
  private readonly destroyRef = inject(DestroyRef);

  readonly showBrowser = signal(true);

  ngOnInit(): void {
    const timer = setInterval(() => this.store.tick(), TICK_MS);
    this.destroyRef.onDestroy(() => {
      clearInterval(timer);
      void this.hub.stop();
    });

    // REST first, socket second. The snapshot the hub pushes on connect makes
    // this redundant in the normal case — but if the socket is slow, blocked by
    // a proxy, or fails entirely, the dashboard still paints real values and the
    // status bar is left to say plainly that the link is down.
    void this.primeFromRest();
    void this.hub.start();
  }

  toggleBrowser(): void {
    this.showBrowser.update((visible) => !visible);
  }

  async unsubscribe(id: string): Promise<void> {
    try {
      await this.api.unsubscribe(id);
      // The hub's NodesChanged already removes the card. Re-syncing as well
      // keeps the browse panel's monitored markers correct without it having to
      // guess what the card did.
      await this.hub.resync();
    } catch {
      // Nothing to roll back: the node is still on screen and still updating,
      // which is the honest outcome of a request that did not take effect.
    }
  }

  private async primeFromRest(): Promise<void> {
    try {
      const nodes = await this.api.nodes();
      if (this.store.nodes().length === 0) {
        this.store.applySnapshot({
          connection: {
            state: 'connecting',
            endpointUrl: '',
            changedAt: new Date().toISOString(),
            attempt: 0,
          },
          nodes,
          serverTime: new Date().toISOString(),
        });
      }
    } catch {
      // The status bar already reports the link as down; a second complaint here
      // would add nothing.
    }
  }
}
