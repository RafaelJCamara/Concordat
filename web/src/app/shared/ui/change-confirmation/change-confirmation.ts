import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { HlmButton } from '@spartan-ng/helm/button';

let nextId = 0;

/**
 * The second click of changing something about a subject: what it will do, in the registry's
 * terms, and a button that does it.
 *
 * <b>It carries no wording of its own about the change.</b> What an owner change records, and what
 * a policy change does and does not touch, is different for each and was measured against the
 * real registry rather than read off its code, so each editor projects its own sentences into
 * this frame (ADR-032). The frame is the part that is the same: the heading naming exactly what
 * will happen, the list of consequences, and one button that does it beside one that goes back.
 *
 * The first click asks and the second does it, and what is named here is a snapshot of the form
 * (the editor withdraws it if anything that would be sent is edited), so the second click can only
 * send what the first showed. Same shape as the confirmations for registering and creating.
 */
@Component({
  selector: 'cd-change-confirmation',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton],
  host: { class: 'block' },
  template: `
    <div
      class="border-warning/40 bg-warning/5 space-y-3 rounded-lg border p-4"
      role="group"
      [attr.aria-labelledby]="titleId"
    >
      <p [id]="titleId" class="text-foreground text-sm font-semibold break-words">
        {{ heading() }}
      </p>

      <ul class="text-muted-foreground list-disc space-y-1 ps-5 text-sm">
        <ng-content />
      </ul>

      <div class="flex flex-wrap gap-2">
        <button hlmBtn type="button" size="sm" [disabled]="busy()" (click)="confirmed.emit()">
          {{ busy() ? busyLabel() : confirmLabel() }}
        </button>
        <button hlmBtn type="button" variant="ghost" size="sm" (click)="declined.emit()">
          {{ declineLabel() }}
        </button>
      </div>
    </div>
  `,
})
export class ChangeConfirmation {
  /** The question, naming exactly what will happen and to what. */
  readonly heading = input.required<string>();

  /** The button that does it, as an answer to the heading: "Yes, change the owner". */
  readonly confirmLabel = input.required<string>();

  /** The same button while the registry has not answered. */
  readonly busyLabel = input.required<string>();

  readonly busy = input(false);

  /** The button that goes back. "Keep editing" when there is a form behind it; a removal has none. */
  readonly declineLabel = input('Keep editing');

  readonly confirmed = output();
  readonly declined = output();

  protected readonly titleId = `change-confirmation-title-${nextId++}`;
}
