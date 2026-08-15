import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/**
 * What a status means, rather than what colour it is.
 *
 * Named for the reading and not the pigment — `success`, not `green` — because the point of
 * a token set is that the pigment can change. A caller that asked for green would have to
 * be revisited when the light theme darkened it; a caller that asked for "this is fine"
 * never does.
 */
export type StatusTone = 'success' | 'warning' | 'destructive' | 'info' | 'neutral';

const TONE_CLASSES: Record<StatusTone, string> = {
  success: 'bg-success/10 text-success',
  warning: 'bg-warning/10 text-warning',
  destructive: 'bg-destructive/10 text-destructive',
  info: 'bg-info/10 text-info',
  neutral: 'bg-muted text-muted-foreground',
};

const DOT_CLASSES: Record<StatusTone, string> = {
  success: 'bg-success',
  warning: 'bg-warning',
  destructive: 'bg-destructive',
  info: 'bg-info',
  neutral: 'bg-muted-foreground',
};

/**
 * A status, as a tinted pill with a dot.
 *
 * The prototype's `StatusBadge`. Its three hardcoded states (active / error / missing)
 * become a tone, because this registry has more than three: a version can be awaiting
 * approval, a subject can be deprecated, a contract can be advisory rather than enforced.
 * Hardcoding the labels here would mean editing this file every time a screen learned a new
 * state, which is how a shared component stops being shared.
 *
 * <b>The tint is never the only signal.</b> Each pill carries its own text, so the
 * distinction survives greyscale, a projector, and the eight percent of men who would
 * otherwise read "success" and "destructive" as the same badge. That is also why the
 * pulsing dot is opt-in: motion draws the eye to one row, and every row pulsing draws it
 * nowhere.
 */
@Component({
  selector: 'cd-status-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'inline-flex' },
  template: `
    <span
      class="inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-medium"
      [class]="toneClass()"
    >
      <span aria-hidden="true" class="size-1.5 rounded-full" [class]="dotClass()"></span>
      <ng-content />
    </span>
  `,
})
export class StatusBadge {
  readonly tone = input<StatusTone>('neutral');

  /** Draws the eye to a live state. Off by default — see the note on motion above. */
  readonly pulse = input<boolean>(false);

  protected readonly toneClass = computed(() => TONE_CLASSES[this.tone()]);

  protected readonly dotClass = computed(() =>
    this.pulse() ? `${DOT_CLASSES[this.tone()]} animate-pulse-dot` : DOT_CLASSES[this.tone()],
  );
}
