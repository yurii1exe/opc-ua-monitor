/**
 * Formatting helpers shared by the value readouts.
 *
 * The service already sends a `displayValue` it has rendered itself, and that is
 * what gets shown — the browser does not reformat numbers it did not measure.
 * These functions handle only what is genuinely presentational: how much of a
 * long value fits on a card, and how old a reading is right now.
 */

/** `14:07:32.184` — no date, because every reading on screen is from today. */
export function timeOfDay(iso: string | undefined): string {
  if (!iso) return '--:--:--.---';

  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;

  const pad = (n: number, width = 2) => String(n).padStart(width, '0');

  return (
    `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}` +
    `.${pad(date.getMilliseconds(), 3)}`
  );
}

/**
 * How long ago, in the shortest form that is still unambiguous.
 *
 * The age of a reading is the single most useful number on an instrument panel:
 * a plausible-looking value that stopped updating four minutes ago is the
 * failure mode a dashboard exists to make visible.
 */
export function age(iso: string | undefined, now: number): string {
  if (!iso) return '—';

  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return '—';

  const ms = Math.max(0, now - then);

  if (ms < 1000) return `${ms}ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`;
  if (ms < 3_600_000) return `${Math.floor(ms / 60_000)}m${Math.floor((ms % 60_000) / 1000)}s`;
  return `${Math.floor(ms / 3_600_000)}h${Math.floor((ms % 3_600_000) / 60_000)}m`;
}

/** Seconds since a timestamp, one decimal place. Used for connection timers. */
export function secondsSince(iso: string | undefined, now: number): number {
  if (!iso) return 0;
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return 0;
  return Math.max(0, (now - then) / 1000);
}

/**
 * Truncates in the middle, keeping both ends.
 *
 * Node ids are long and the informative parts are at the two ends — the
 * namespace at the front and the tag name at the back. Clipping the tail, which
 * is what CSS ellipsis does, throws away the half that identifies the tag.
 */
export function elide(value: string, max: number): string {
  if (value.length <= max) return value;
  const head = Math.ceil((max - 1) / 2);
  const tail = Math.floor((max - 1) / 2);
  return `${value.slice(0, head)}…${value.slice(value.length - tail)}`;
}

/** Numeric value of a reading, or null when the node is not numeric. */
export function asNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

/**
 * Significant figures shown on a card. The detail pane shows the value the
 * server rendered, in full.
 */
const CARD_SIGNIFICANT_FIGURES = 6;

/**
 * A number at a precision a human can read at a glance.
 *
 * The service sends the value at full double precision, and it is right to —
 * a diagnostic tool and the REST API both want every digit. A card does not:
 * `0.05753638806392586` in 23px type is a wall of digits that says nothing an
 * operator can act on, and it does not fit. Six significant figures is more
 * than any real instrument resolves to, and it keeps the leading digits — the
 * ones that carry the magnitude — at a size that reads across a room.
 *
 * Non-numeric values are elided rather than reformatted, because the server's
 * rendering of an array or an enum is already the considered one.
 */
export function compact(value: unknown, fallback: string, max = 20): string {
  const numeric = asNumber(value);
  if (numeric === null) return elide(fallback, max);

  if (Number.isInteger(numeric)) return String(numeric);

  // Exponential for magnitudes where fixed notation would be all zeros or
  // unreadably long, which is where the SI-prefixed instrument would switch
  // units.
  const magnitude = Math.abs(numeric);
  if (magnitude !== 0 && (magnitude < 1e-4 || magnitude >= 1e9)) {
    return numeric.toExponential(3);
  }

  return String(Number(numeric.toPrecision(CARD_SIGNIFICANT_FIGURES)));
}
