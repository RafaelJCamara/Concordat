import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { checkOwnerEdit } from '../../../domain/registry/subject-settings';
import { ChangeConfirmation } from './change-confirmation';

const INPUT =
  'border-input bg-background focus-visible:ring-ring flex h-9 w-full rounded-md border px-3 py-1 text-sm focus-visible:ring-1 focus-visible:outline-none';

/**
 * Changing a subject's owner.
 *
 * <b>Everything the confirmation says was measured, not read.</b> A change records one audit entry
 * (`SUBJECT_UPDATED`, "owner changed", attributed to the account that made it) and says nothing
 * about which owner it was or became; it notifies nobody (only deprecating does); and it moves
 * nothing but the owner — the name, format, content model, policy and versions are what they were.
 * The owner is not an access control either: anybody holding `subject:write` may change it whoever
 * owns the subject. `e2e/subject-settings.spec.ts` and the registry's own tests pin each sentence,
 * so a registry that starts recording more has to change this text in the same change.
 *
 * <b>The form blocks on two things.</b> What the registry certainly refuses (an owner over 256
 * characters after its trim) and what there is no request for (a blank field, or the owner the
 * page already shows). Neither is a registry refusal, and the second is the one place this is
 * knowingly stricter than the registry — see `checkOwnerEdit` and ADR-032.
 *
 * <b>It sends nothing itself.</b> Presentational like `CreateSubjectConfirmation`: it reports
 * what was confirmed and the page decides what to do with it, and what the registry answered
 * comes back in through `error` and `saving`.
 */
@Component({
  selector: 'cd-subject-owner-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, HlmAlertImports, HlmButton, ChangeConfirmation],
  host: { class: 'block' },
  template: `
    <section
      class="border-border bg-card space-y-4 rounded-xl border p-5"
      aria-labelledby="subject-owner-editor-title"
    >
      <div>
        <h2 id="subject-owner-editor-title" class="text-foreground text-base font-semibold">
          Change owner
        </h2>
        <p class="text-muted-foreground mt-1 text-sm">
          Who answers for <span class="font-mono break-words">{{ subject() }}</span
          >: a person or, more often, a team.
        </p>
      </div>

      @if (error(); as failure) {
        <div hlmAlert variant="destructive" role="alert">
          <h3 hlmAlertTitle>The registry refused this change</h3>
          <p hlmAlertDescription>{{ failure }}</p>
        </div>
      }

      <form [formGroup]="form" (ngSubmit)="ask()" class="space-y-4" novalidate>
        <div class="space-y-1.5">
          <label class="text-sm font-medium" for="subject-owner-new">New owner</label>
          <input
            id="subject-owner-new"
            [formControl]="owner"
            autocomplete="off"
            aria-describedby="subject-owner-new-help"
            [attr.aria-invalid]="refusal() ? 'true' : null"
            [class]="input"
          />
          <div id="subject-owner-new-help" aria-live="polite">
            @if (refusal(); as finding) {
              <p class="text-destructive text-xs">{{ finding.message }}</p>
            } @else if (advice(); as finding) {
              <p class="text-muted-foreground text-xs">{{ finding.message }}</p>
            }
          </div>
        </div>

        @if (confirming(); as next) {
          <cd-change-confirmation
            [heading]="
              'Change the owner of ' + subject() + ' from ' + current() + ' to ' + next + '?'
            "
            confirmLabel="Yes, change the owner"
            busyLabel="Changing…"
            [busy]="saving()"
            (confirmed)="confirmed.emit(next)"
            (declined)="confirmation.set(null)"
          >
            <li>
              <span class="text-foreground">Only the owner changes.</span>
              The name, the format, the content model, the compatibility policy and every version
              stay as they are.
            </li>
            <li>
              It is a label, not access: anyone who may change this subject may change it, whoever
              owns it.
            </li>
            <li>
              The audit log gets one entry, <span class="font-mono">SUBJECT_UPDATED</span>, reading
              “owner changed” and naming the account that made it. It does not record the previous
              owner or the new one.
            </li>
            <li>Nobody is notified.</li>
          </cd-change-confirmation>
        } @else {
          <div class="flex items-center gap-2">
            <button hlmBtn type="submit" [disabled]="edit().send === null || saving()">
              Review change
            </button>
            <button hlmBtn type="button" variant="ghost" (click)="cancelled.emit()">Cancel</button>
          </div>
        }
      </form>
    </section>
  `,
})
export class SubjectOwnerEditor {
  /** The subject being changed. */
  readonly subject = input.required<string>();

  /** The owner the page last read from the registry. */
  readonly current = input.required<string>();

  /** The registry's refusal of the last attempt, in its own words (its `detail`), or null. */
  readonly error = input<string | null>(null);

  /** Whether the registry has not yet answered a confirmed change. */
  readonly saving = input(false);

  /** The owner to send, after the second click. */
  readonly confirmed = output<string>();
  readonly cancelled = output();

  protected readonly input = INPUT;

  protected readonly owner = new FormControl('', { nonNullable: true });

  /**
   * A group around the one control, because `(ngSubmit)` is the output of the form *directives*:
   * a bare `<form>` has none, the submit event reaches nothing, and "Review change" does nothing.
   */
  protected readonly form = new FormGroup({ owner: this.owner });

  /**
   * The control's own events as a signal: a `computed` over a control's value would never
   * recompute, since an `AbstractControl` is not a signal.
   */
  private readonly events = toSignal(this.owner.events);

  protected readonly edit = computed(() => {
    this.events();

    return checkOwnerEdit(this.current(), this.owner.value);
  });

  /**
   * Whether the person has edited the field, which is when it earns the right to complain.
   *
   * It opens holding the owner already shown, so complaining on open — that this is already the
   * owner — would be telling somebody off for not having started.
   */
  private readonly edited = computed(() => {
    this.events();

    return this.owner.dirty;
  });

  protected readonly refusal = computed(
    () => this.edit().findings.find((f) => f.severity === 'refusal') ?? null,
  );

  protected readonly advice = computed(() =>
    this.edited() ? (this.edit().findings.find((f) => f.severity === 'advice') ?? null) : null,
  );

  /**
   * What the person said they would send when they asked, or null if they have not.
   *
   * <b>The confirmation belongs to one exact request.</b> {@link confirming} compares it with the
   * form as it is now, so editing the field after the first click withdraws it and the second can
   * only send what the first showed.
   */
  protected readonly confirmation = signal<string | null>(null);

  protected readonly confirming = computed(() => {
    const send = this.edit().send;

    return send !== null && this.confirmation() === send ? send : null;
  });

  constructor() {
    // Starts on the owner already shown, so changing it is an edit and not a blank to fill. Only
    // while the person has not typed: a refresh after somebody else's change must not overwrite
    // what is being written.
    effect(() => {
      const current = this.current();

      untracked(() => {
        if (!this.owner.dirty) {
          this.owner.setValue(current, { emitEvent: true });
        }
      });
    });
  }

  /** The first click: asks for confirmation rather than changing. */
  protected ask(): void {
    this.owner.markAsDirty();

    const send = this.edit().send;

    if (send !== null && !this.saving()) {
      this.confirmation.set(send);
    }
  }
}
