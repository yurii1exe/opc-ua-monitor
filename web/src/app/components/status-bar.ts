import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { secondsSince } from '../core/format';
import { MonitorStore } from '../core/monitor-store';

/** Text and colour for the headline state. */
const LABELS: Record<string, { text: string; hint: string }> = {
  connected: { text: 'CONNECTED', hint: 'Subscription live, values streaming.' },
  reconnecting: {
    text: 'RECONNECTING',
    hint: 'The OPC session dropped. The service is retrying on a backoff.',
  },
  degraded: {
    text: 'DEGRADED',
    hint: 'The dashboard is connected to the service, but the service has no OPC session.',
  },
  offline: {
    text: 'LINK LOST',
    hint: 'The dashboard cannot reach the service. Values on screen are the last ones received.',
  },
};

/**
 * The header. Two independent links, both stated, plus the backoff.
 *
 * The design rule here is that nothing is hidden behind a spinner. A spinner
 * says "wait" and stops there; an operator needs to know which of the two hops
 * is broken, how many times it has been retried, why it failed and how long
 * until the next attempt — because those are the facts that decide whether to
 * wait, restart something, or go and look at the server.
 */
@Component({
  selector: 'app-status-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="bar" [class]="store.overall()">
      <div class="brand">
        <span class="mark"></span>
        <span class="title">OPC&nbsp;UA&nbsp;MONITOR</span>
      </div>

      <div class="state" [title]="label().hint">
        <span class="dot"></span>
        <span class="text">{{ label().text }}</span>
      </div>

      <dl class="facts">
        <div class="fact endpoint" [title]="connection()?.endpointUrl ?? ''">
          <dt>endpoint</dt>
          <dd>{{ connection()?.endpointUrl ?? '—' }}</dd>
        </div>

        <!--
          Dimmed and marked when the dashboard link is down. Everything known
          about the OPC session arrives over that link, so once it drops this
          field is the last thing the service said, not the current state — and
          a confident green "connected" on a dashboard that is no longer being
          told anything is the single most misleading thing this bar could show.
        -->
        <div class="fact" [title]="opcTitle()">
          <dt>opc session</dt>
          <dd class="opc" [class]="connection()?.state ?? 'disconnected'" [class.unknown]="!linked()">
            {{ connection()?.state ?? '—' }}{{ linked() ? '' : ' (last known)' }}
          </dd>
        </div>

        <div class="fact">
          <dt>dashboard link</dt>
          <dd class="link" [class]="store.link()">{{ store.link() }}</dd>
        </div>

        <div class="fact">
          <dt>nodes</dt>
          <dd>{{ store.nodeCount() }}</dd>
        </div>

        <div class="fact">
          <dt>updates/s</dt>
          <dd>{{ store.updateRate().toFixed(1) }}</dd>
        </div>

        @if (store.alarmCount() > 0) {
          <div class="fact warn-fact">
            <dt>out of band</dt>
            <dd>{{ store.alarmCount() }}</dd>
          </div>
        }

        @if (store.badCount() > 0) {
          <div class="fact bad-fact">
            <dt>bad quality</dt>
            <dd>{{ store.badCount() }}</dd>
          </div>
        }

        @if (store.quietCount() > 0) {
          <div
            class="fact warn-fact"
            title="Nodes that have not reported a change in over two minutes. Expected for a constant tag; worth investigating for one that should be moving."
          >
            <dt>quiet</dt>
            <dd>{{ store.quietCount() }}</dd>
          </div>
        }
      </dl>
    </header>

    @if (showBackoff()) {
      <div class="backoff" role="status">
        <span class="attempt">attempt {{ connection()?.attempt ?? 0 }}</span>
        <span class="elapsed">waiting {{ elapsed().toFixed(1) }}s</span>

        @if (retryIn(); as total) {
          <span class="progress" [attr.aria-label]="'Next retry in ' + remaining().toFixed(1) + ' seconds'">
            <span class="fill" [style.width.%]="progress()"></span>
          </span>
          <span class="next">next retry in {{ remaining().toFixed(1) }}s</span>
        }

        @if (connection()?.detail; as detail) {
          <span class="detail" [title]="detail">{{ detail }}</span>
        }
      </div>
    }

    @if (store.link() !== 'connected') {
      <div class="backoff link-lost" role="status">
        <span class="attempt">dashboard link {{ store.link() }}</span>
        <span class="detail">
          The values below are the last ones received and are no longer updating.
        </span>
      </div>
    }
  `,
  styleUrl: './status-bar.scss',
})
export class StatusBar {
  readonly store = inject(MonitorStore);

  readonly connection = this.store.connection;

  readonly label = computed(() => LABELS[this.store.overall()]);

  readonly linked = computed(() => this.store.link() === 'connected');

  readonly opcTitle = computed(() =>
    this.linked()
      ? "The service's session with the OPC UA server."
      : 'The last state the service reported before the dashboard link dropped. It may have changed since.',
  );

  readonly showBackoff = computed(() => {
    const state = this.connection()?.state;
    return state === 'reconnecting' || state === 'connecting' || state === 'faulted';
  });

  readonly elapsed = computed(() => secondsSince(this.connection()?.changedAt, this.store.now()));

  /**
   * The backoff delay, taken from the service's own detail string.
   *
   * The service already writes the delay into the detail it publishes, so it is
   * read from there rather than adding a field to a contract that is otherwise
   * stable. Best effort by design: if the wording ever changes, the countdown
   * and the bar quietly disappear and the attempt count, the elapsed timer and
   * the detail text — which are structured fields — carry on working.
   */
  readonly retryIn = computed<number | null>(() => {
    const match = /retrying in ([\d.]+)s/i.exec(this.connection()?.detail ?? '');
    if (!match) return null;

    const seconds = Number.parseFloat(match[1]);
    return Number.isFinite(seconds) ? seconds : null;
  });

  readonly remaining = computed(() => Math.max(0, (this.retryIn() ?? 0) - this.elapsed()));

  readonly progress = computed(() => {
    const total = this.retryIn();
    if (!total) return 0;
    return Math.min(100, (this.elapsed() / total) * 100);
  });
}
