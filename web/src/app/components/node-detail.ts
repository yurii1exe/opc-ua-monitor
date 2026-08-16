import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { age, timeOfDay } from '../core/format';
import { NodeView } from '../core/monitor-store';
import { TrendChart } from './trend-chart';

/**
 * The selected node, at a size where the shape of the signal is actually
 * readable.
 *
 * The card grid answers "is everything alright"; this answers "what is this one
 * doing". The statistics are computed over the retained window only and are
 * labelled as such, because a mean over "however much history the browser
 * happens to be holding" would otherwise read as a process figure.
 */
@Component({
  selector: 'app-node-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TrendChart],
  template: `
    <section class="detail" [class.alarm]="view().withinBand === false">
      <div class="head">
        <div class="titles">
          <h2>{{ view().node.displayName }}</h2>
          <code class="id">{{ view().node.id }}</code>
        </div>

        <div class="live">
          <span class="value">{{ view().current?.displayValue ?? '—' }}</span>
          @if (view().node.unit) {
            <span class="unit">{{ view().node.unit }}</span>
          }
        </div>
      </div>

      <div class="plot">
        @if (view().numeric) {
          <app-trend-chart
            [samples]="view().samples"
            [minimum]="view().node.minimum"
            [maximum]="view().node.maximum"
            [alarm]="view().withinBand === false"
            [label]="view().node.displayName"
          />
          <div class="axis">
            <span class="hi">{{ format(high()) }}</span>
            <span class="lo">{{ format(low()) }}</span>
          </div>
        } @else if (view().samples.length === 0) {
          <p class="nonnumeric">
            No readings yet. The chart fills in as values arrive — the service reads every monitored
            node once on connect, so this should not stay empty for long while the session is up.
          </p>
        } @else {
          <p class="nonnumeric">
            Values on this node are not numeric, so there is nothing to plot. The card shows update
            activity instead.
          </p>
        }
      </div>

      <dl class="stats">
        <div><dt>window min</dt><dd>{{ format(low()) }}</dd></div>
        <div><dt>window max</dt><dd>{{ format(high()) }}</dd></div>
        <div><dt>window mean</dt><dd>{{ format(mean()) }}</dd></div>
        <div><dt>samples</dt><dd>{{ view().samples.length }}</dd></div>
        <div><dt>span</dt><dd>{{ span() }}</dd></div>
        <div><dt>updates</dt><dd>{{ view().updates }}</dd></div>
        <div><dt>quality</dt><dd [class]="view().current?.qualitySeverity ?? ''">{{ view().current?.quality ?? '—' }}</dd></div>
        <div><dt>timestamp</dt><dd>{{ timeOfDay(view().current?.timestamp) }}</dd></div>
        <div><dt>age</dt><dd>{{ ageText() }}</dd></div>
        @if (view().node.minimum !== undefined || view().node.maximum !== undefined) {
          <div>
            <dt>band</dt>
            <dd [class.alarm]="view().withinBand === false">{{ band() }}</dd>
          </div>
        }
      </dl>
    </section>
  `,
  styleUrl: './node-detail.scss',
})
export class NodeDetail {
  readonly view = input.required<NodeView>();
  readonly now = input.required<number>();

  readonly timeOfDay = timeOfDay;

  private readonly values = computed(() =>
    this.view()
      .samples.map((s) => s.v)
      .filter((v): v is number => v !== null),
  );

  readonly low = computed(() => (this.values().length ? Math.min(...this.values()) : null));
  readonly high = computed(() => (this.values().length ? Math.max(...this.values()) : null));

  readonly mean = computed(() => {
    const values = this.values();
    if (values.length === 0) return null;
    return values.reduce((sum, v) => sum + v, 0) / values.length;
  });

  readonly span = computed(() => {
    const samples = this.view().samples;
    if (samples.length < 2) return '—';

    const seconds = (samples[samples.length - 1].t - samples[0].t) / 1000;
    if (seconds < 90) return `${seconds.toFixed(0)}s`;
    return `${(seconds / 60).toFixed(1)}m`;
  });

  readonly ageText = computed(() => age(this.view().current?.timestamp, this.now()));

  readonly band = computed(() => {
    const { minimum, maximum } = this.view().node;
    return `${minimum === undefined ? '−∞' : minimum} … ${maximum === undefined ? '∞' : maximum}`;
  });

  /**
   * Six significant figures, not a fixed number of decimals. A tank level of
   * 0.19 and a pressure of 4210 both have to be legible in the same column, and
   * a fixed precision makes one of them wrong.
   */
  format(value: number | null): string {
    if (value === null) return '—';
    if (Number.isInteger(value)) return String(value);
    return Number(value.toPrecision(6)).toString();
  }
}
