import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { Sample } from '../core/monitor-store';

interface Band {
  y: number;
  height: number;
}

interface Plot {
  /** One path per unbroken run of samples. */
  paths: string[];
  band: Band | null;
  lastX: number;
  lastY: number;
  hasPoint: boolean;
  low: number;
  high: number;
  spanSeconds: number;
  gridY: number[];
}

/** Internal coordinate space. Scaled to fit by the viewBox; nothing depends on pixels. */
const W = 320;
const H = 96;
const PAD_TOP = 6;
const PAD_BOTTOM = 6;

/**
 * A gap larger than this multiple of the typical interval breaks the line rather
 * than being drawn across.
 */
const GAP_FACTOR = 4;

/** Below this total time span, samples are spaced by index rather than by time. */
const DEGENERATE_SPAN_MS = 1_000;

/**
 * Time-series plot for one node.
 *
 * Hand-drawn SVG rather than a charting library, for three reasons that all
 * matter here: a chart library is the largest dependency this app would have and
 * would be used for one chart type; the instrument look — thin lines, no
 * animation, no tooltips competing for attention — is easier to get by drawing
 * it than by fighting a library's defaults; and an SVG with a fixed viewBox
 * renders identically at any size, which is what makes the screenshots
 * reproducible.
 */
@Component({
  selector: 'app-trend-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <svg
      class="chart"
      [attr.viewBox]="viewBox"
      preserveAspectRatio="none"
      role="img"
      [attr.aria-label]="ariaLabel()"
    >
      @if (plot(); as p) {
        @if (p.band) {
          <rect class="band" x="0" [attr.y]="p.band.y" [attr.width]="width" [attr.height]="p.band.height" />
        }

        @for (y of p.gridY; track y) {
          <line class="grid" x1="0" [attr.x2]="width" [attr.y1]="y" [attr.y2]="y" />
        }

        @for (d of p.paths; track $index) {
          <path class="trace" [class.alarm]="alarm()" [attr.d]="d" />
        }

      }
    </svg>

    <!--
      The head marker is HTML positioned in percent, not an SVG circle. The
      viewBox is stretched to the element with preserveAspectRatio="none" — which
      is what lets one chart geometry serve a 52px card and a 600px panel — and
      anything drawn inside it is stretched with it, so a circle comes out as an
      ellipse whose eccentricity depends on the panel size.
    -->
    @if (plot(); as p) {
      @if (p.hasPoint) {
        <span
          class="head"
          [class.alarm]="alarm()"
          [style.left.%]="(p.lastX / width) * 100"
          [style.top.%]="(p.lastY / height) * 100"
        ></span>
      }
    }
  `,
  styles: `
    :host {
      display: block;
      position: relative;
    }

    .chart {
      display: block;
      width: 100%;
      height: 100%;
      overflow: visible;
    }

    .band {
      fill: var(--band);
    }

    .grid {
      stroke: var(--grid);
      stroke-width: 1;
      vector-effect: non-scaling-stroke;
    }

    .trace {
      fill: none;
      stroke: var(--trace);
      stroke-width: 1.5;
      stroke-linejoin: round;
      stroke-linecap: round;
      vector-effect: non-scaling-stroke;
    }

    .trace.alarm {
      stroke: var(--alarm);
    }

    .head {
      position: absolute;
      width: 5px;
      height: 5px;
      margin: -2.5px 0 0 -2.5px;
      border-radius: 50%;
      background: var(--trace);
      pointer-events: none;
    }

    .head.alarm {
      background: var(--alarm);
    }
  `,
})
export class TrendChart {
  readonly samples = input.required<Sample[]>();
  readonly minimum = input<number | undefined>(undefined);
  readonly maximum = input<number | undefined>(undefined);
  readonly alarm = input(false);
  readonly label = input('');

  readonly width = W;
  readonly height = H;
  readonly viewBox = `0 0 ${W} ${H}`;

  readonly plot = computed<Plot | null>(() => {
    const points = this.samples().filter((s): s is Sample & { v: number } => s.v !== null);
    if (points.length === 0) return null;

    const times = points.map((p) => p.t);
    const values = points.map((p) => p.v);

    const tMin = Math.min(...times);
    const tMax = Math.max(...times);
    const tSpan = tMax - tMin;

    const { low, high } = this.range(values);
    const vSpan = Math.max(high - low, Number.EPSILON);

    // A node that has not changed since the client attached has every sample at
    // essentially the same instant, and scaling by that span collapses the whole
    // series onto the left edge — a single dot that reads as a broken chart
    // rather than as a steady value. Falling back to index spacing draws the
    // flat line the operator expects, and it is honest: the samples are real,
    // only their spacing is nominal.
    const spread = tSpan >= DEGENERATE_SPAN_MS;
    const x = (t: number, index: number) =>
      spread ? ((t - tMin) / tSpan) * W : (index / Math.max(points.length - 1, 1)) * W;
    const y = (v: number) => PAD_TOP + (1 - (v - low) / vSpan) * (H - PAD_TOP - PAD_BOTTOM);

    const gapThreshold = spread ? medianInterval(times) * GAP_FACTOR : Number.POSITIVE_INFINITY;

    const paths: string[] = [];
    let current: string[] = [];

    for (let i = 0; i < points.length; i++) {
      const broken = i > 0 && times[i] - times[i - 1] > gapThreshold;

      if (broken && current.length > 0) {
        paths.push(current.join(' '));
        current = [];
      }

      current.push(
        `${current.length === 0 ? 'M' : 'L'}${x(times[i], i).toFixed(2)},${y(values[i]).toFixed(2)}`,
      );
    }

    // A lone point has no line. Drawing it as a full-width flat segment says
    // "this value, for as long as I have been watching", which is what it means.
    if (points.length === 1) {
      current.push(`L${W},${y(values[0]).toFixed(2)}`);
    }

    if (current.length > 0) paths.push(current.join(' '));

    return {
      paths,
      band: this.bandRect(low, vSpan),
      lastX: x(times[times.length - 1], points.length - 1),
      lastY: y(values[values.length - 1]),
      hasPoint: true,
      low,
      high,
      spanSeconds: tSpan / 1000,
      // Quarter lines only. A denser grid on a 96-unit-tall chart stops being a
      // reference and starts being texture.
      gridY: [0.25, 0.5, 0.75].map((f) => PAD_TOP + f * (H - PAD_TOP - PAD_BOTTOM)),
    };
  });

  readonly ariaLabel = computed(() => {
    const p = this.plot();
    if (!p) return `${this.label()}: no numeric data`;
    return `${this.label()}: ${p.low.toPrecision(4)} to ${p.high.toPrecision(4)} over ${p.spanSeconds.toFixed(0)} seconds`;
  });

  /**
   * Vertical range of the plot.
   *
   * Autoscaled to the data rather than pinned to the configured band. A tank
   * that lives between 0.4 and 0.6 of its 0–1 band would otherwise draw as a
   * flat line through the middle, hiding exactly the movement worth watching.
   * The band is still drawn, as a shaded region, so the context is not lost.
   *
   * A dead-flat signal gets an artificial range, because dividing by a zero span
   * gives a line at the top of the chart rather than through the middle of it.
   */
  private range(values: number[]): { low: number; high: number } {
    let low = Math.min(...values);
    let high = Math.max(...values);

    if (high - low < Number.EPSILON) {
      const nudge = Math.max(Math.abs(high) * 0.05, 0.5);
      low -= nudge;
      high += nudge;
    } else {
      const headroom = (high - low) * 0.08;
      low -= headroom;
      high += headroom;
    }

    return { low, high };
  }

  private bandRect(low: number, vSpan: number): Band | null {
    const min = this.minimum();
    const max = this.maximum();
    if (min === undefined && max === undefined) return null;

    const plotHeight = H - PAD_TOP - PAD_BOTTOM;
    const toY = (v: number) => PAD_TOP + (1 - (v - low) / vSpan) * plotHeight;

    const top = max === undefined ? PAD_TOP : toY(max);
    const bottom = min === undefined ? H - PAD_BOTTOM : toY(min);

    const clampedTop = Math.max(PAD_TOP, Math.min(top, H - PAD_BOTTOM));
    const clampedBottom = Math.max(PAD_TOP, Math.min(bottom, H - PAD_BOTTOM));

    const height = clampedBottom - clampedTop;
    return height > 0.5 ? { y: clampedTop, height } : null;
  }
}

/**
 * Median rather than mean, so one long gap does not raise the threshold enough
 * to hide the next one.
 */
function medianInterval(times: number[]): number {
  if (times.length < 3) return Number.POSITIVE_INFINITY;

  const deltas: number[] = [];
  for (let i = 1; i < times.length; i++) deltas.push(times[i] - times[i - 1]);

  deltas.sort((a, b) => a - b);
  return Math.max(deltas[Math.floor(deltas.length / 2)], 1);
}
