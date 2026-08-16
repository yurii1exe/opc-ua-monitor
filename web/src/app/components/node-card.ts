import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { age, compact, elide, timeOfDay } from '../core/format';
import { NodeView, QUIET_AFTER_MS } from '../core/monitor-store';
import { TrendChart } from './trend-chart';

/**
 * One node, rendered as an instrument: label, reading, units, provenance, trend.
 *
 * The layout order is the order the information is needed in. Value first and
 * largest; then how old it is, because a stale value is the failure a monitor
 * exists to expose; then quality, then the trend. The node id sits at the
 * bottom in a dim monospace because it identifies the tag but nobody reads it
 * during normal operation.
 */
@Component({
  selector: 'app-node-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TrendChart],
  template: `
    <article
      class="card"
      [class.selected]="selected()"
      [class.alarm]="view().withinBand === false"
      [class.bad]="severity() === 'bad'"
      [class.stale]="stale()"
      [class.silent]="view().current === null"
      (click)="pick.emit(view().node.id)"
      (keydown.enter)="pick.emit(view().node.id)"
      tabindex="0"
      role="button"
      [attr.aria-label]="'Node ' + view().node.displayName"
    >
      <header>
        <span class="name" [title]="view().node.displayName">{{ view().node.displayName }}</span>
        <span class="flags">
          @if (view().withinBand === false) {
            <span class="flag alarm-flag" title="Outside the configured band">OUT OF BAND</span>
          }
          <span class="flag quality" [class]="severity()" [title]="'Quality: ' + quality()">{{
            quality()
          }}</span>
        </span>
        <button
          class="drop"
          type="button"
          title="Stop monitoring this node"
          aria-label="Unsubscribe"
          (click)="$event.stopPropagation(); drop.emit(view().node.id)"
        >
          ×
        </button>
      </header>

      <div class="readout">
        <span class="value" [class]="sizeClass()" [title]="view().current?.displayValue ?? ''">{{
          value()
        }}</span>
        @if (view().node.unit) {
          <span class="unit">{{ view().node.unit }}</span>
        }
      </div>

      <div class="chart-slot">
        @if (view().numeric) {
          <app-trend-chart
            [samples]="view().samples"
            [minimum]="view().node.minimum"
            [maximum]="view().node.maximum"
            [alarm]="view().withinBand === false"
            [label]="view().node.displayName"
          />
        } @else {
          <div class="ticks" [attr.aria-label]="'Update activity for ' + view().node.displayName">
            @for (tick of ticks(); track $index) {
              <span class="tick" [style.opacity]="tick"></span>
            }
          </div>
        }
      </div>

      <footer>
        <span class="ts" title="Source timestamp, or server timestamp when the device gave none">{{
          timestamp()
        }}</span>
        <span
          class="age"
          [class.warn]="stale()"
          title="Time since that timestamp. A subscription reports changes only, so a constant tag legitimately ages."
          >{{ ageText() }}</span
        >
        @if (view().node.minimum !== undefined || view().node.maximum !== undefined) {
          <span class="band" title="Configured band">{{ band() }}</span>
        }
        <span class="id" [title]="view().node.id">{{ shortId() }}</span>
      </footer>
    </article>
  `,
  styleUrl: './node-card.scss',
})
export class NodeCard {
  readonly view = input.required<NodeView>();
  readonly now = input.required<number>();
  readonly selected = input(false);

  readonly pick = output<string>();
  readonly drop = output<string>();

  readonly value = computed(() => {
    const current = this.view().current;
    if (!current) return '—';
    return compact(current.value, current.displayValue);
  });

  /**
   * Type size chosen from the length of the value, the way a fixed-width
   * instrument display shrinks digits to fit rather than clipping them.
   *
   * The alternative — one size plus `text-overflow: ellipsis` — silently hides
   * the end of a value, and on a monitor a half-shown number is worse than a
   * small one. Sizes are stepped rather than continuous so the grid does not
   * shimmer as a value crosses a boundary each second.
   */
  readonly sizeClass = computed(() => {
    const length = this.value().length;
    if (length <= 9) return 'xl';
    if (length <= 13) return 'lg';
    if (length <= 19) return 'md';
    return 'sm';
  });

  readonly quality = computed(() => this.view().current?.quality ?? 'no data');

  /**
   * Empty rather than "bad" when there is no reading at all.
   *
   * A node that has not reported yet has unknown quality, not bad quality, and
   * painting it red says the server rejected something when in fact nothing has
   * been asked yet. The distinction matters most during a reconnect, when every
   * card is briefly empty and a screen full of red would be actively misleading.
   */
  readonly severity = computed(() => this.view().current?.qualitySeverity ?? '');
  readonly timestamp = computed(() => timeOfDay(this.view().current?.timestamp));
  readonly ageText = computed(() => age(this.view().current?.timestamp, this.now()));

  /**
   * Whether this node has gone quiet. Dims the card and highlights the age
   * rather than raising an alarm — see the note on {@link QUIET_AFTER_MS}.
   */
  readonly stale = computed(() => {
    const current = this.view().current;
    if (!current) return false;
    return this.now() - new Date(current.timestamp).getTime() > QUIET_AFTER_MS;
  });

  readonly band = computed(() => {
    const { minimum, maximum } = this.view().node;
    const low = minimum === undefined ? '−∞' : String(minimum);
    const high = maximum === undefined ? '∞' : String(maximum);
    return `${low} … ${high}`;
  });

  readonly shortId = computed(() => elide(this.view().node.id, 30));

  /**
   * Liveness strip for nodes whose values are not numbers — timestamps, enum
   * states, strings. A trend line would be meaningless for those, but "is it
   * still updating" is exactly as important, so the arrival of each of the last
   * few readings is shown instead, fading with age.
   */
  readonly ticks = computed(() => {
    const recent = this.view().samples.slice(-32);
    return recent.map((_, index) => 0.25 + (0.75 * (index + 1)) / recent.length);
  });
}
