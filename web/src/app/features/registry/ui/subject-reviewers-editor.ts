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
import { checkReviewersEdit, REVIEWERS_MAX } from '../../../domain/registry/subject-settings';
import { ChangeConfirmation } from '../../../shared/ui/change-confirmation/change-confirmation';

const TEXTAREA =
  'border-input bg-background focus-visible:ring-ring flex min-h-24 w-full rounded-md border px-3 py-2 font-mono text-sm focus-visible:ring-1 focus-visible:outline-none';

/**
 * Naming who may approve or reject a subject's held versions (ADR-043).
 *
 * <b>One reviewer per line</b>, because an actor id is any text and an API key's label can hold a
 * comma. The field opens holding the set already named, so changing it is an edit and not a blank to
 * fill, and an empty field is a real request: it clears the set.
 *
 * <b>Every sentence in the confirmation was measured, not read.</b> A change records one audit entry
 * (`SUBJECT_REVIEWERS_CHANGED`, naming the account and both sets); it notifies nobody; it takes effect
 * at once, for versions already waiting as well as later ones; and it is a rule about who decides, not
 * a lock, because the same scope edits it. The registry's own tests and `e2e/subject-reviewers.spec.ts`
 * pin each, so a registry that starts recording or notifying more has to change this text with it.
 *
 * <b>It sends nothing itself</b>, like the owner editor: it reports what was confirmed, and what the
 * registry answered comes back in through `error` and `saving`.
 */
@Component({
  selector: 'cd-subject-reviewers-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, HlmAlertImports, HlmButton, ChangeConfirmation],
  host: { class: 'block' },
  template: `
    <section
      class="border-border bg-card space-y-4 rounded-xl border p-5"
      aria-labelledby="subject-reviewers-editor-title"
    >
      <div>
        <h2 id="subject-reviewers-editor-title" class="text-foreground text-base font-semibold">
          Change reviewers
        </h2>
        <p class="text-muted-foreground mt-1 text-sm">
          Who may approve or reject a version of
          <span class="font-mono break-words">{{ subject() }}</span> that is held for review. Leave
          it empty and any administrator may, as for every subject by default.
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
          <label class="text-sm font-medium" for="subject-reviewers-new">
            Reviewers, one per line
          </label>
          <textarea
            id="subject-reviewers-new"
            [formControl]="reviewers"
            rows="4"
            autocomplete="off"
            spellcheck="false"
            aria-describedby="subject-reviewers-new-help"
            [attr.aria-invalid]="refusal() ? 'true' : null"
            [class]="textarea"
          ></textarea>
          <div id="subject-reviewers-new-help" aria-live="polite" class="space-y-1">
            <p class="text-muted-foreground text-xs">
              A person's email address, or the label of an API key. At most {{ max }}. Names are
              compared without regard to case.
            </p>
            @if (refusal(); as finding) {
              <p class="text-destructive text-xs">{{ finding.message }}</p>
            } @else {
              @for (finding of advice(); track finding.code) {
                <p class="text-muted-foreground text-xs">{{ finding.message }}</p>
              }
            }
          </div>
        </div>

        @if (confirming(); as next) {
          <cd-change-confirmation
            [heading]="
              next.length === 0
                ? 'Clear the reviewers of ' + subject() + '?'
                : 'Let only ' + next.join(', ') + ' decide ' + subject() + '?'
            "
            [confirmLabel]="
              next.length === 0 ? 'Yes, clear the reviewers' : 'Yes, change the reviewers'
            "
            busyLabel="Changing…"
            [busy]="saving()"
            (confirmed)="confirmed.emit(next)"
            (declined)="confirmation.set(null)"
          >
            @if (next.length === 0) {
              <li>
                <span class="text-foreground">Any administrator may approve or reject</span> a held
                version of this subject again.
              </li>
            } @else {
              <li>
                <span class="text-foreground">Only the people named may approve or reject</span> a
                held version of this subject. Anyone else, an administrator included, is refused.
              </li>
            }
            <li>
              It applies at once, to versions that are already waiting as well as to later ones. The
              name, the format, the policy and every version stay as they are.
            </li>
            <li>
              It routes the decision and does not lock it: anyone who may administer this subject
              may change the reviewers.
            </li>
            <li>
              The audit log gets one entry,
              <span class="font-mono">SUBJECT_REVIEWERS_CHANGED</span>, naming the account that made
              it and recording the previous set and the new one.
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
export class SubjectReviewersEditor {
  /** The subject being changed. */
  readonly subject = input.required<string>();

  /** The reviewers the page last read from the registry. */
  readonly current = input.required<readonly string[]>();

  /** Who is signed in, so the form can say when a set would leave them unable to decide. */
  readonly actor = input<string | null>(null);

  /** The registry's refusal of the last attempt, in its own words (its `detail`), or null. */
  readonly error = input<string | null>(null);

  /** Whether the registry has not yet answered a confirmed change. */
  readonly saving = input(false);

  /** The set to send, after the second click. Empty clears it. */
  readonly confirmed = output<readonly string[]>();
  readonly cancelled = output();

  protected readonly textarea = TEXTAREA;
  protected readonly max = REVIEWERS_MAX;

  protected readonly reviewers = new FormControl('', { nonNullable: true });

  /** A group around the one control, because `(ngSubmit)` is the output of the form directives. */
  protected readonly form = new FormGroup({ reviewers: this.reviewers });

  /** The control's own events as a signal: a `computed` over a control's value would never recompute. */
  private readonly events = toSignal(this.reviewers.events);

  protected readonly edit = computed(() => {
    this.events();

    return checkReviewersEdit(this.current(), this.reviewers.value, this.actor());
  });

  /** Whether the person has edited the field, which is when it earns the right to complain. */
  private readonly edited = computed(() => {
    this.events();

    return this.reviewers.dirty;
  });

  protected readonly refusal = computed(
    () => this.edit().findings.find((f) => f.severity === 'refusal') ?? null,
  );

  protected readonly advice = computed(() =>
    this.edited() ? this.edit().findings.filter((f) => f.severity === 'advice') : [],
  );

  /**
   * What the person said they would send when they asked, or null if they have not.
   *
   * <b>The confirmation belongs to one exact request.</b> {@link confirming} compares it with the form
   * as it is now, so editing the field after the first click withdraws it and the second can only send
   * what the first showed.
   */
  protected readonly confirmation = signal<readonly string[] | null>(null);

  protected readonly confirming = computed(() => {
    const send = this.edit().send;
    const asked = this.confirmation();

    return send !== null && asked !== null && sameList(asked, send) ? send : null;
  });

  constructor() {
    // Starts on the set already named, one to a line. Only while the person has not typed: a refresh
    // after somebody else's change must not overwrite what is being written.
    effect(() => {
      const current = this.current();

      untracked(() => {
        if (!this.reviewers.dirty) {
          this.reviewers.setValue(current.join('\n'), { emitEvent: true });
        }
      });
    });
  }

  /** The first click: asks for confirmation rather than changing. */
  protected ask(): void {
    this.reviewers.markAsDirty();

    const send = this.edit().send;

    if (send !== null && !this.saving()) {
      this.confirmation.set(send);
    }
  }
}

function sameList(a: readonly string[], b: readonly string[]): boolean {
  return a.length === b.length && a.every((value, i) => value === b[i]);
}
