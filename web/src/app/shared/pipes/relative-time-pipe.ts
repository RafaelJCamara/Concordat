import { Pipe, type PipeTransform } from '@angular/core';

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;
const WEEK = 7 * DAY;
const MONTH = 30 * DAY;
const YEAR = 365 * DAY;

const UNITS: readonly (readonly [number, Intl.RelativeTimeFormatUnit])[] = [
  [YEAR, 'year'],
  [MONTH, 'month'],
  [WEEK, 'week'],
  [DAY, 'day'],
  [HOUR, 'hour'],
  [MINUTE, 'minute'],
];

/**
 * "3 days ago", for timestamps in a list.
 *
 * Pure, so it recomputes when its input changes and not on a timer. A self-ticking pipe
 * would keep the whole application awake once a second to move one label from "58 seconds
 * ago" to "a minute ago", which under zoneless change detection means a scheduled
 * re-render for no user-visible gain.
 *
 * Always pair it with the exact time in a `title` — "2 months ago" is the right thing to
 * scan a list with and the wrong thing to reconcile an incident against, and the next
 * question after "recently?" is always "when exactly?".
 */
@Pipe({ name: 'cdRelativeTime' })
export class RelativeTimePipe implements PipeTransform {
  private readonly format = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });

  transform(value: Date | null | undefined, now: Date = new Date()): string {
    if (value === null || value === undefined) {
      return '';
    }

    const elapsed = value.getTime() - now.getTime();
    const magnitude = Math.abs(elapsed);

    for (const [size, unit] of UNITS) {
      if (magnitude >= size) {
        return this.format.format(Math.round(elapsed / size), unit);
      }
    }

    return this.format.format(0, 'second');
  }
}
