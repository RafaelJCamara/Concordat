import { describe, expect, it } from 'vitest';
import { RelativeTimePipe } from './relative-time-pipe';

// The pipe's own job is choosing a unit and a count; `Intl` does the wording. So the
// assertions compare against `Intl`'s output for an expected (count, unit) pair rather than
// against an English string — otherwise the whole file would fail on a machine with a
// different default locale, which is a test failure that teaches nobody anything.

const pipe = new RelativeTimePipe();
const format = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });

const NOW = new Date('2026-08-13T12:00:00.000Z');
const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/** A timestamp `offset` milliseconds away from `NOW`; negative is in the past. */
function at(offset: number): Date {
  return new Date(NOW.getTime() + offset);
}

function reads(offset: number): string {
  return pipe.transform(at(offset), NOW);
}

function as(value: number, unit: Intl.RelativeTimeFormatUnit): string {
  return format.format(value, unit);
}

describe('RelativeTimePipe', () => {
  describe('given nothing to format', () => {
    it('renders an empty cell for null', () => {
      // A subject with no active version has no `registeredAt`, and the table renders the
      // value straight into a cell. "Invalid Date" or "now" would both be lies.
      expect(pipe.transform(null)).toBe('');
    });

    it('renders an empty cell for undefined', () => {
      expect(pipe.transform(undefined)).toBe('');
    });
  });

  describe('choosing a unit', () => {
    it('says now for anything under a minute', () => {
      // The smallest unit is deliberately a minute: a pure pipe cannot tick, so a "seconds
      // ago" label would be wrong within seconds and stay wrong until something else
      // re-rendered the row.
      expect(reads(-30_000)).toBe(as(0, 'second'));
      expect(reads(-999)).toBe(as(0, 'second'));
    });

    it('says now for a timestamp a few seconds in the future', () => {
      // Clock skew between the registry and the browser. A version registered "in 4
      // seconds" is a clock difference, not a schedule.
      expect(reads(4_000)).toBe(as(0, 'second'));
    });

    it('steps up a unit exactly on the boundary', () => {
      expect(reads(-MINUTE)).toBe(as(-1, 'minute'));
      expect(reads(-HOUR)).toBe(as(-1, 'hour'));
      expect(reads(-DAY)).toBe(as(-1, 'day'));
      expect(reads(-7 * DAY)).toBe(as(-1, 'week'));
      expect(reads(-30 * DAY)).toBe(as(-1, 'month'));
      expect(reads(-365 * DAY)).toBe(as(-1, 'year'));
    });

    it('stays in the smaller unit just below the boundary', () => {
      // The pair of assertions that catches an off-by-one in the comparison: `>=` versus
      // `>` moves every one of these into the next unit up.
      expect(reads(-59 * MINUTE)).toBe(as(-59, 'minute'));
      expect(reads(-6 * DAY)).toBe(as(-6, 'day'));
      expect(reads(-29 * DAY)).toBe(as(-4, 'week'));
      expect(reads(-364 * DAY)).toBe(as(-12, 'month'));
    });

    it('rounds to the nearest whole unit rather than truncating', () => {
      // 100 minutes is closer to two hours than to one, and "1 hour ago" for something
      // registered an hour and forty minutes back reads as fresher than it is.
      expect(reads(-100 * MINUTE)).toBe(as(-2, 'hour'));
    });

    it('handles the future in the same units', () => {
      expect(reads(2 * DAY)).toBe(as(2, 'day'));
      expect(reads(3 * HOUR)).toBe(as(3, 'hour'));
    });
  });

  describe('the reference time', () => {
    it('defaults to the current clock', () => {
      // The template calls the pipe with one argument. If the default were a fixed time —
      // module load, say — every row would drift further from the truth the longer a tab
      // stayed open.
      const fiveMinutesAgo = new Date(Date.now() - 5 * MINUTE);

      expect(pipe.transform(fiveMinutesAgo)).toBe(as(-5, 'minute'));
    });

    it('can be pinned, so a list renders one consistent instant', () => {
      expect(pipe.transform(at(-2 * DAY), NOW)).toBe(as(-2, 'day'));
    });
  });

  it('is pure, so the same inputs always give the same answer', () => {
    // `pure` is the default and is not stated in the decorator. Marking it impure would
    // keep the whole application awake re-evaluating every timestamp on every change
    // detection run, which under zoneless is a scheduled re-render for no visible gain.
    expect(reads(-3 * DAY)).toBe(reads(-3 * DAY));
  });
});
