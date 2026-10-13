import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import { HlmButton } from '@spartan-ng/helm/button';

/**
 * A secret the registry will never show again, and the one chance to take it.
 *
 * Used wherever the registry returns a credential exactly once — an API key's secret, a
 * webhook's signing key — because the failure it guards against is the same in both: the person
 * closes the page, the registry holds only a hash, and the only recovery is to revoke and issue
 * another.
 *
 * <b>It stays until dismissed.</b> A toast would take the one copy with it. The field is
 * read-only and selects itself on focus, so copying by hand works where the clipboard API does
 * not (no permission, or an origin that is not secure) — and a failed copy says so rather than
 * quietly leaving the button on "Copy" as though nothing had been asked.
 *
 * <b>Presentational.</b> The secret arrives as an input and leaves nowhere; clearing it from
 * memory is the owning store's job, signalled by `dismissed`.
 */
@Component({
  selector: 'cd-one-time-secret',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton],
  host: { class: 'block' },
  template: `
    <div
      class="border-warning/40 bg-warning/5 space-y-3 rounded-xl border p-5"
      role="alert"
      aria-labelledby="one-time-secret-heading"
    >
      <h2 id="one-time-secret-heading" class="text-foreground text-sm font-semibold">
        {{ heading() }}
      </h2>

      <div class="text-muted-foreground text-sm"><ng-content /></div>

      <div class="flex flex-wrap items-center gap-2">
        <input
          readonly
          [value]="secret()"
          [attr.aria-label]="label()"
          class="border-input bg-background focus-visible:ring-ring flex h-9 min-w-0 flex-1 rounded-md border px-3 py-1 font-mono text-sm focus-visible:ring-1 focus-visible:outline-none"
          (focus)="selectAll($event)"
        />
        <button type="button" hlmBtn size="sm" (click)="copy()">
          {{ copied() ? 'Copied' : 'Copy' }}
        </button>
        <button type="button" hlmBtn variant="outline" size="sm" (click)="done()">
          I have saved it
        </button>
      </div>

      @if (failed()) {
        <p class="text-destructive text-sm" role="status">
          Could not copy automatically. Select the field and copy it by hand.
        </p>
      }
    </div>
  `,
})
export class OneTimeSecret {
  /** What to call the secret in the panel's heading. */
  readonly heading = input.required<string>();

  /** The secret itself. Held only as long as the owner keeps passing it. */
  readonly secret = input.required<string>();

  /** The accessible name of the field, so a screen reader says what is in it. */
  readonly label = input<string>('Secret');

  /** The person has taken the secret. The owner should clear it from memory. */
  readonly dismissed = output<void>();

  protected readonly copied = signal(false);
  protected readonly failed = signal(false);

  protected selectAll(event: Event): void {
    (event.target as HTMLInputElement).select();
  }

  protected async copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.secret());
      this.copied.set(true);
      this.failed.set(false);
    } catch {
      this.copied.set(false);
      this.failed.set(true);
    }
  }

  protected done(): void {
    this.copied.set(false);
    this.failed.set(false);
    this.dismissed.emit();
  }
}
