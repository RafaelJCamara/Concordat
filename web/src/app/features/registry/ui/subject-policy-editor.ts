import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  effect,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import {
  COMPATIBILITY_MODES,
  COMPATIBILITY_SURFACES,
  isInherited,
  type CompatibilityMode,
  type CompatibilityPolicy,
  type CompatibilitySurface,
} from '../../../domain/registry/compatibility';
import {
  checkPolicyEdit,
  describePolicy,
  type PolicyDraft,
} from '../../../domain/registry/subject-settings';
import { ChangeConfirmation } from './change-confirmation';

const INPUT =
  'border-input bg-background focus-visible:ring-ring flex h-9 w-full rounded-md border px-3 py-1 text-sm focus-visible:ring-1 focus-visible:outline-none';

/**
 * Changing a subject's compatibility policy.
 *
 * <b>Two selects that both default to "inherit"</b>, as the create screen has, so the registry's
 * "both or neither" rule has no separate switch to disagree with: choosing one and not the other
 * is the one refusal there is, and it is shown beside the field.
 *
 * <b>Inheriting and choosing the current default are different requests</b> (`isInherited`), and
 * the confirmation says which one this is. A pinned policy stays put when the environment's default
 * changes; an inherited one follows it, at registration and everywhere else the registry judges a
 * subject (ADR-033). Until that ADR it did not: registration, the dry run and the diff judged a
 * subject with no policy of its own by the registry's built-in default whatever the environment's
 * was, and this screen said so. The sentence now says what the registry does, and
 * `e2e/subject-settings.spec.ts` holds it against an environment whose default is not the
 * built-in one, so it cannot go stale in silence in either direction.
 *
 * <b>What the confirmation says changes, and what it does not, was measured.</b> The new policy
 * judges the *next* registration. Versions already registered are not judged again, and a version
 * held at the approval gate stays held when the policy is relaxed. The change is not written to
 * the audit log (a gap in the registry, pinned by an integration test that goes red when it is
 * closed) and notifies nobody.
 */
@Component({
  selector: 'cd-subject-policy-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, HlmAlertImports, HlmButton, ChangeConfirmation],
  host: { class: 'block' },
  template: `
    <section
      class="border-border bg-card space-y-4 rounded-xl border p-5"
      aria-labelledby="subject-policy-editor-title"
    >
      <div>
        <h2 id="subject-policy-editor-title" class="text-foreground text-base font-semibold">
          Change compatibility policy
        </h2>
        <p class="text-muted-foreground mt-1 text-sm">
          What the registry will let <span class="font-mono break-words">{{ subject() }}</span>
          register next: who must keep working, and how much must keep working.
        </p>
      </div>

      @if (error(); as failure) {
        <div hlmAlert variant="destructive" role="alert">
          <h3 hlmAlertTitle>The registry refused this change</h3>
          <p hlmAlertDescription>{{ failure }}</p>
        </div>
      }

      <form [formGroup]="form" (ngSubmit)="ask()" class="space-y-4" novalidate>
        <fieldset class="space-y-2">
          <legend class="sr-only">Compatibility policy</legend>
          <div class="grid gap-4 sm:grid-cols-2">
            <div class="space-y-1.5">
              <label class="text-muted-foreground text-xs" for="subject-policy-mode">Mode</label>
              <select id="subject-policy-mode" formControlName="mode" [class]="input">
                <option value="">Inherit the environment default</option>
                @for (mode of modes; track mode) {
                  <option [value]="mode">{{ mode }}</option>
                }
              </select>
            </div>
            <div class="space-y-1.5">
              <label class="text-muted-foreground text-xs" for="subject-policy-surface"
                >Surface</label
              >
              <select id="subject-policy-surface" formControlName="surface" [class]="input">
                <option value="">Inherit the environment default</option>
                @for (surface of surfaces; track surface) {
                  <option [value]="surface">{{ surface }}</option>
                }
              </select>
            </div>
          </div>
          <div aria-live="polite">
            @if (refusal(); as finding) {
              <p class="text-destructive text-xs">{{ finding.message }}</p>
            } @else if (advice(); as finding) {
              <p class="text-muted-foreground text-xs">{{ finding.message }}</p>
            } @else {
              <p class="text-muted-foreground text-xs">
                Both or neither. Leaving both on the default removes this subject’s own policy;
                choosing them pins it, which is not the same thing.
              </p>
            }
          </div>
        </fieldset>

        @if (confirming(); as next) {
          <cd-change-confirmation
            [heading]="heading(next)"
            [confirmLabel]="isInheriting(next) ? 'Yes, inherit' : 'Yes, change the policy'"
            busyLabel="Changing…"
            [busy]="saving()"
            (confirmed)="confirmed.emit(next)"
            (declined)="confirmation.set(null)"
          >
            <li>
              <span class="text-foreground"
                >It takes effect for the next version registered on this subject.</span
              >
              Versions already registered are not judged again, and a version held at the approval
              gate stays held even if this is more permissive than the policy that held it.
            </li>
            @if (isInheriting(next)) {
              <li>
                <span class="text-foreground"
                  >Inheriting is not the same as choosing the current default.</span
                >
                A subject with no policy of its own follows the environment’s default when it
                changes, where a pinned one keeps its policy whatever the default becomes.
              </li>
              <li>
                <span class="text-foreground"
                  >From the next version, this subject is judged by its environment’s default.</span
                >
                Registering a version, checking a schema without registering it, comparing two
                versions, impact analysis and promotion all read it. The environment’s own settings
                say what it is.
              </li>
            } @else {
              <li>
                <span class="text-foreground">The subject is pinned to it.</span>
                If the environment’s default changes later, this subject does not follow.
              </li>
            }
            <li>This change is not written to the audit log.</li>
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
export class SubjectPolicyEditor {
  /** The subject being changed. */
  readonly subject = input.required<string>();

  /** The policy the page last read from the registry. Both members null when it inherits. */
  readonly current = input.required<CompatibilityPolicy>();

  /** The registry's refusal of the last attempt, in its own words (its `detail`), or null. */
  readonly error = input<string | null>(null);

  /** Whether the registry has not yet answered a confirmed change. */
  readonly saving = input(false);

  /** The policy to send after the second click. Both members null to inherit. */
  readonly confirmed = output<CompatibilityPolicy>();
  readonly cancelled = output();

  protected readonly input = INPUT;
  protected readonly modes = COMPATIBILITY_MODES;
  protected readonly surfaces = COMPATIBILITY_SURFACES;

  private readonly builder = inject(FormBuilder);

  protected readonly form = this.builder.nonNullable.group({
    // Empty is "inherit". Both or neither is the registry's rule.
    mode: ['' as CompatibilityMode | ''],
    surface: ['' as CompatibilitySurface | ''],
  });

  private readonly events = toSignal(this.form.events);

  private readonly draft = computed((): PolicyDraft => {
    this.events();

    const { mode, surface } = this.form.getRawValue();

    return { mode: mode === '' ? null : mode, surface: surface === '' ? null : surface };
  });

  protected readonly edit = computed(() => checkPolicyEdit(this.current(), this.draft()));

  private readonly edited = computed(() => {
    this.events();

    return this.form.dirty;
  });

  protected readonly refusal = computed(
    () => this.edit().findings.find((f) => f.severity === 'refusal') ?? null,
  );

  protected readonly advice = computed(() =>
    this.edited() ? (this.edit().findings.find((f) => f.severity === 'advice') ?? null) : null,
  );

  /**
   * What the person said they would send when they asked, as a snapshot of the policy, or null.
   *
   * The confirmation belongs to one exact request: {@link confirming} compares it with the form
   * as it is now, so choosing something else after the first click withdraws it.
   */
  protected readonly confirmation = signal<string | null>(null);

  protected readonly confirming = computed(() => {
    const send = this.edit().send;

    return send !== null && this.confirmation() === JSON.stringify(send) ? send : null;
  });

  constructor() {
    // Starts on the policy already shown, so changing it is an edit. Only while the person has not
    // chosen: a refresh after somebody else's change must not overwrite what is being picked.
    effect(() => {
      const { mode, surface } = this.current();

      untracked(() => {
        if (!this.form.dirty) {
          this.form.setValue({ mode: mode ?? '', surface: surface ?? '' });
        }
      });
    });
  }

  protected isInheriting(policy: CompatibilityPolicy): boolean {
    return isInherited(policy);
  }

  /** The confirmation's question: what exactly would happen, to what. */
  protected heading(policy: CompatibilityPolicy): string {
    return isInherited(policy)
      ? `Remove the policy of ${this.subject()}, so that it inherits?`
      : `Pin ${this.subject()} to ${describePolicy(policy)}?`;
  }

  /** The first click: asks for confirmation rather than changing. */
  protected ask(): void {
    this.form.markAsDirty();

    const send = this.edit().send;

    if (send !== null && !this.saving()) {
      this.confirmation.set(JSON.stringify(send));
    }
  }
}
