import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { HlmButton } from '@spartan-ng/helm/button';

/**
 * The second click of creating a subject.
 *
 * <b>A subject is the one thing on this screen that cannot be taken back, and the prototype's
 * "immutable id" confirmation existed to say exactly that.</b> Every claim below was checked
 * against the real registry rather than read off its code: `PATCH` ignores a new name, format or
 * content model and answers 200 with them unchanged; there is no delete, retiring is terminal;
 * and a retired name answers 409 when somebody tries to create it again, so the name stays taken
 * for good. What *can* change is the owner and the compatibility policy — through the API, which
 * is said plainly, because this app has no screen for either and a confirmation that promised
 * "you can change that later" would be sending somebody to a button that is not there.
 *
 * The first click asks, naming exactly what will exist and where; the second does it. What is
 * named here is a snapshot of the form (the page withdraws the confirmation if any of it is
 * edited), so the second click can only create what the first showed.
 */
@Component({
  selector: 'cd-create-subject-confirmation',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton],
  host: { class: 'block' },
  template: `
    <div
      class="border-warning/40 bg-warning/5 space-y-3 rounded-lg border p-4"
      role="group"
      aria-labelledby="create-subject-confirmation-title"
    >
      <p id="create-subject-confirmation-title" class="text-foreground text-sm font-semibold">
        Create <span class="font-mono break-words">{{ name() }}</span> in
        <span class="font-mono">{{ environment() }}</span
        >?
      </p>

      <ul class="text-muted-foreground list-disc space-y-1 ps-5 text-sm">
        <li>
          <span class="text-foreground">The name is permanent.</span>
          A subject cannot be renamed or deleted, and a retired one keeps its name for good, so
          nobody can ever create <span class="font-mono break-words">{{ name() }}</span> again.
        </li>
        <li>
          <span class="text-foreground">So are its format and content model:</span>
          {{ formatLabel() }}, {{ contentModelLabel() }}.
        </li>
        <li>
          The owner and the compatibility policy can be changed later, but only through the API —
          this app has no screen for either yet.
        </li>
      </ul>

      <div class="flex flex-wrap gap-2">
        <button hlmBtn type="button" size="sm" [disabled]="busy()" (click)="confirmed.emit()">
          {{ busy() ? 'Creating…' : 'Yes, create it' }}
        </button>
        <button hlmBtn type="button" variant="ghost" size="sm" (click)="declined.emit()">
          Keep editing
        </button>
      </div>
    </div>
  `,
})
export class CreateSubjectConfirmation {
  /** The name that will be created, trimmed as the registry will store it. */
  readonly name = input.required<string>();
  readonly environment = input.required<string>();
  readonly formatLabel = input.required<string>();
  readonly contentModelLabel = input.required<string>();

  readonly busy = input(false);

  readonly confirmed = output();
  readonly declined = output();
}
