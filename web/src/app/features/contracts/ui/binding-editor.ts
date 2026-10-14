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
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { checkBindingEdit, type BindingChange } from '../../../domain/contracts/binding-edit';
import {
  formatSubjectRefs,
  type ConsumeBinding,
  type EnforcementMode,
  type PublishBinding,
} from '../../../domain/contracts/contract';
import { ChangeConfirmation } from '../../../shared/ui/change-confirmation/change-confirmation';

const INPUT =
  'border-input bg-background focus-visible:ring-ring flex h-9 w-full rounded-md border px-3 py-1 text-sm focus-visible:ring-1 focus-visible:outline-none';

/** The binding being worked on, and which kind it is. */
export type BindingTarget =
  | { readonly kind: 'publish'; readonly binding: PublishBinding }
  | { readonly kind: 'consume'; readonly binding: ConsumeBinding };

/**
 * Changing or removing one binding of a contract.
 *
 * <b>Two clicks, and the second says what it does in the registry's terms.</b> What each sentence
 * claims was measured against the real registry (ADR-035) and is held by
 * `e2e/contracts.spec.ts`, so it cannot go stale in silence: that only the subjects and the
 * precedence change, that an overlap is refused with the registry's own reason, that a removed
 * binding stops governing its route (a route nothing else governs resolves as ungoverned, `OFF`),
 * that one audit entry of its own action is written, and that nobody is notified.
 *
 * <b>Only the subjects and the precedence can be changed.</b> The exchange, the pattern and the
 * virtual host are what the binding *is*: changing them is a different binding governing a
 * different route. That is a removal and an addition, each said for what it is.
 *
 * <b>It blocks on the shape and leaves the rule to the registry.</b> Whether the result would
 * overlap another binding carrying different subjects is the intersection of two topic patterns,
 * which the browser cannot see; the registry's refusal is shown here in its own words and leaves
 * the binding as it was.
 *
 * The confirmation is a snapshot of what would be sent: choosing something else after the first
 * click withdraws it, so the second click can only send what the first showed.
 */
@Component({
  selector: 'cd-binding-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, HlmAlertImports, HlmButton, ChangeConfirmation],
  host: { class: 'block' },
  template: `
    <section
      class="border-border bg-card space-y-4 rounded-xl border p-5"
      [attr.aria-labelledby]="'binding-editor-title'"
    >
      <div>
        <h2 id="binding-editor-title" class="text-foreground text-base font-semibold">
          {{ mode() === 'change' ? 'Change a binding' : 'Remove a binding' }}
        </h2>
        <p class="text-muted-foreground mt-1 text-sm">
          <span class="font-mono break-words">{{ label() }}</span>
        </p>
      </div>

      @if (error(); as failure) {
        <div hlmAlert variant="destructive" role="alert">
          <h3 hlmAlertTitle>
            {{ mode() === 'change' ? 'The registry refused this change' : 'Could not remove it' }}
          </h3>
          <p hlmAlertDescription>{{ failure }}</p>
        </div>
      }

      @if (mode() === 'change') {
        <form [formGroup]="form" (ngSubmit)="ask()" class="space-y-4" novalidate>
          <div class="space-y-1.5">
            <label class="text-sm font-medium" for="binding-subjects">Subjects</label>
            <input
              id="binding-subjects"
              formControlName="subjects"
              autocomplete="off"
              [class]="input + ' font-mono'"
            />
            <p class="text-muted-foreground text-xs">
              Comma or space separated. Add a version with &#64;: orders.created&#64;2,
              orders.created&#64;>=2. Without one, a subject means latest.
            </p>
          </div>

          @if (target().kind === 'publish') {
            <div class="max-w-48 space-y-1.5">
              <label class="text-sm font-medium" for="binding-precedence">
                Precedence <span class="text-muted-foreground font-normal">· optional</span>
              </label>
              <input
                id="binding-precedence"
                type="number"
                formControlName="precedence"
                [class]="input"
              />
            </div>
            <p class="text-muted-foreground text-xs">
              It only separates two overlapping bindings when both have a value and the values
              differ. Clear it to give the binding none.
            </p>
          }

          <div aria-live="polite">
            @if (refusal(); as finding) {
              <p class="text-destructive text-xs">{{ finding.message }}</p>
            } @else if (advice(); as finding) {
              <p class="text-muted-foreground text-xs">{{ finding.message }}</p>
            }
          </div>

          @if (confirming(); as next) {
            <cd-change-confirmation
              [heading]="'Change the binding of ' + label() + '?'"
              confirmLabel="Yes, change the binding"
              busyLabel="Changing…"
              [busy]="saving()"
              (confirmed)="changed.emit(next)"
              (declined)="confirmation.set(null)"
            >
              <li>
                <span class="text-foreground">Only the subjects and the precedence change.</span>
                {{ target().kind === 'publish' ? 'The exchange, the pattern' : 'The queue' }} and
                the virtual host are what the binding is, and stay.
              </li>
              <li>
                <span class="text-foreground"
                  >The registry answers with it from the next resolution.</span
                >
                A service that already resolved the route keeps its answer until its cache expires
                (the .NET SDK’s <span class="font-mono">ContractTtl</span>, one minute by default).
                The registry refuses a change that would leave this binding overlapping another one
                that carries different subjects, when no precedence separates them, and says which.
              </li>
              <li>
                The audit log gets one entry,
                <span class="font-mono">CONTRACT_BINDING_CHANGED</span>, naming the account that
                made it and what changed. Nobody is notified.
              </li>
            </cd-change-confirmation>
          } @else {
            <div class="flex items-center gap-2">
              <button hlmBtn type="submit" [disabled]="edit().send === null || saving()">
                Review change
              </button>
              <button hlmBtn type="button" variant="ghost" (click)="cancelled.emit()">
                Cancel
              </button>
            </div>
          }
        </form>
      } @else {
        <cd-change-confirmation
          [heading]="'Remove the binding of ' + label() + '?'"
          confirmLabel="Yes, remove the binding"
          declineLabel="Keep it"
          busyLabel="Removing…"
          [busy]="saving()"
          (confirmed)="removed.emit()"
          (declined)="cancelled.emit()"
        >
          <li>
            @if (target().kind === 'publish') {
              <span class="text-foreground"
                >Routes matching <span class="font-mono">{{ pattern() }}</span> on
                <span class="font-mono">{{ exchange() }}</span> stop being governed by this
                binding.</span
              >
              If nothing else governs them (another binding of this contract, or another contract),
              the registry resolves them as ungoverned (<span class="font-mono">OFF</span>) from the
              next resolution.
            } @else {
              <span class="text-foreground"
                >The queue <span class="font-mono">{{ queue() }}</span> is no longer expected to
                receive these subjects under this contract.</span
              >
            }
            @if (enforcement() === 'ENFORCE') {
              This contract enforces, so a message on
              {{ target().kind === 'publish' ? 'those routes' : 'that queue' }}
              stops being refused because of it.
            }
          </li>
          <li>
            A service that already resolved
            {{ target().kind === 'publish' ? 'those routes' : 'that queue' }} keeps its answer until
            its cache expires (the .NET SDK’s <span class="font-mono">ContractTtl</span>, one minute
            by default).
          </li>
          <li>
            It cannot be undone, but a binding with the same details can be added again; it gets a
            new id.
          </li>
          <li>
            The audit log gets one entry,
            <span class="font-mono">CONTRACT_BINDING_REMOVED</span>, naming the account that made
            it. Nobody is notified.
          </li>
        </cd-change-confirmation>
      }
    </section>
  `,
})
export class BindingEditor {
  /** The binding, and which kind it is. */
  readonly target = input.required<BindingTarget>();

  /** Whether it is being changed or removed. */
  readonly mode = input.required<'change' | 'remove'>();

  /** The contract's own mode, which decides whether removing says it lifts enforcement. */
  readonly enforcement = input.required<EnforcementMode>();

  /** Whether the registry has not yet answered a confirmed request. */
  readonly saving = input(false);

  /** The registry's refusal of the last attempt, in its own words, or null. */
  readonly error = input<string | null>(null);

  /** What to send after the second click of a change. */
  readonly changed = output<BindingChange>();
  readonly removed = output();
  readonly cancelled = output();

  protected readonly input = INPUT;

  protected readonly form = new FormBuilder().nonNullable.group({
    subjects: [''],
    precedence: [null as number | null],
  });

  private readonly events = toSignal(this.form.events);

  protected readonly label = computed(() => {
    const target = this.target();

    return target.kind === 'publish'
      ? `${target.binding.exchange} / ${target.binding.routingKeyPattern}`
      : target.binding.queue;
  });

  protected readonly exchange = computed(() => {
    const target = this.target();
    return target.kind === 'publish' ? target.binding.exchange : '';
  });

  protected readonly pattern = computed(() => {
    const target = this.target();
    return target.kind === 'publish' ? target.binding.routingKeyPattern : '';
  });

  protected readonly queue = computed(() => {
    const target = this.target();
    return target.kind === 'consume' ? target.binding.queue : '';
  });

  private readonly draft = computed(() => {
    this.events();

    const { subjects, precedence } = this.form.getRawValue();

    return { subjects, precedence };
  });

  protected readonly edit = computed(() => {
    const target = this.target();

    return checkBindingEdit(
      {
        subjects: target.binding.subjects,
        precedence: target.kind === 'publish' ? target.binding.precedence : null,
      },
      this.draft(),
      target.kind === 'publish',
    );
  });

  private readonly edited = computed(() => {
    this.events();

    return this.form.dirty;
  });

  protected readonly refusal = computed(() =>
    this.edit().finding?.severity === 'refusal' ? this.edit().finding : null,
  );

  protected readonly advice = computed(() =>
    this.edited() && this.edit().finding?.severity === 'advice' ? this.edit().finding : null,
  );

  /** What the person said they would send when they asked, as a snapshot, or null. */
  protected readonly confirmation = signal<string | null>(null);

  protected readonly confirming = computed(() => {
    const send = this.edit().send;

    return send !== null && this.confirmation() === JSON.stringify(send) ? send : null;
  });

  constructor() {
    // A refusal sends the person back to the form. Left on the confirmation, the second click
    // would send the same refused request again with nothing to edit. Only on the transition from
    // saving to refused, so the confirmation survives the wait and a later edit is not undone.
    let wasSaving = false;

    effect(() => {
      const saving = this.saving();
      const refused = this.error() !== null;

      untracked(() => {
        if (wasSaving && !saving && refused) {
          this.confirmation.set(null);
        }

        wasSaving = saving;
      });
    });

    // Starts on what the binding carries, so changing it is an edit and not a blank form. Reset
    // whenever a different binding is chosen; not while the same one is being edited, so a refresh
    // after somebody else's change cannot overwrite what is being typed.
    effect(() => {
      const target = this.target();

      untracked(() => {
        this.form.reset({
          subjects: formatSubjectRefs(target.binding.subjects),
          precedence: target.kind === 'publish' ? target.binding.precedence : null,
        });
        this.confirmation.set(null);
      });
    });
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
