import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { HlmButton } from '@spartan-ng/helm/button';

/**
 * The second click of registering a version.
 *
 * <b>Registering is the one thing on this screen that cannot be taken back.</b> A version is
 * immutable: its ordinal is allocated for good and is never reused, its document cannot be
 * edited, and there is no delete — a mistake is corrected by registering another version, which
 * the history then carries beside it. That is what the prototype's "immutable id" confirmation
 * existed to say, and it is the same fact in this registry's terms.
 *
 * So the first click asks, naming exactly what will be allocated and where, and the second
 * does it. The ordinal named here is the registry's prediction from the subject it last read,
 * and is worded as one: the answer, which the screen shows once it arrives, is what counts.
 */
@Component({
  selector: 'cd-register-confirmation',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton],
  host: { class: 'block' },
  template: `
    <div
      class="border-warning/40 bg-warning/5 space-y-3 rounded-lg border p-4"
      role="group"
      aria-labelledby="register-confirmation-title"
    >
      <p id="register-confirmation-title" class="text-foreground text-sm font-semibold">
        Register this as
        @if (ordinal(); as next) {
          <span class="font-mono">v{{ next }}</span>
        } @else {
          a new version
        }
        of <span class="font-mono">{{ subject() }}</span> in
        <span class="font-mono">{{ environment() }}</span
        >?
      </p>

      <p class="text-muted-foreground text-sm">
        A version is permanent. Its number is never reused, its document cannot be edited and it
        cannot be deleted; a mistake is fixed by registering another version after it.
        @if (label(); as value) {
          It will carry the label <span class="text-foreground font-mono">{{ value }}</span
          >, which the registry checks against what changed.
        }
      </p>

      <div class="flex flex-wrap gap-2">
        <button hlmBtn type="button" size="sm" [disabled]="busy()" (click)="confirmed.emit()">
          {{ busy() ? 'Registering…' : 'Yes, register it' }}
        </button>
        <button hlmBtn type="button" variant="ghost" size="sm" (click)="declined.emit()">
          Keep editing
        </button>
      </div>
    </div>
  `,
})
export class RegisterConfirmation {
  readonly subject = input.required<string>();
  readonly environment = input.required<string>();

  /** The ordinal the registry is expected to allocate, from the subject as last read. */
  readonly ordinal = input<number | null>(null);

  /** The semantic-version label that will be sent, if any. */
  readonly label = input<string | null>(null);

  readonly busy = input(false);

  readonly confirmed = output();
  readonly declined = output();
}
