import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { IfScope } from '../../../core/auth/if-scope';
import { SessionStore } from '../../../core/auth/session-store';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import type { BindingChange } from '../../../domain/contracts/binding-edit';
import {
  ENFORCEMENT_MODES,
  parseSubjectRefs,
  subjectsRefusal,
  type EnforcementMode,
} from '../../../domain/contracts/contract';
import { CONTRACT_WRITE_SCOPES, grants } from '../../../domain/identity/scope';
import { Icon } from '../../../shared/ui/icon/icon';
import { ContractDetailStore, type ContractAction } from '../application/contract-detail-store';
import { BindingEditor, type BindingTarget } from '../ui/binding-editor';
import { BindingList, type BindingRef } from '../ui/binding-list';
import { ENFORCEMENT_MEANING, EnforcementBadge } from '../ui/enforcement-badge';

const INPUT =
  'border-input bg-background focus-visible:ring-ring flex h-9 w-full rounded-md border px-3 py-1 text-sm focus-visible:ring-1 focus-visible:outline-none';

const SUBJECTS_HELP =
  'Comma or space separated. Add a version with @: orders.created@2, orders.created@>=2. ' +
  'Without one, a subject means latest.';

/**
 * One contract: its bindings, how strictly it is held to, and the ways to change either.
 *
 * <b>Every control that changes something is absent without `contract:write`</b> (ADR-018), and
 * the contract itself is fully readable without it.
 *
 * <b>Moving to `ENFORCE` takes two clicks and says what it does.</b> A contract's mode wins
 * over a service's own configuration in both directions (decisions-pending #30), so this is not
 * a setting on one service: every service that resolves one of this contract's routes starts
 * refusing non-conforming messages. Switching to `MONITOR` or `OFF` is the safe direction and
 * takes one.
 *
 * <b>A binding is only shown once the registry has stored it.</b> It can be refused for an
 * overlap with an existing binding — decided by intersecting the two patterns, which is not
 * something the browser can see — and the refusal's own words are rendered under the form that
 * caused it, leaving the contract and the other forms exactly as they were.
 */
@Component({
  selector: 'cd-contract-detail-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [ContractDetailStore],
  imports: [
    ReactiveFormsModule,
    RouterLink,
    HlmAlertImports,
    HlmButton,
    HlmSkeleton,
    IfScope,
    Icon,
    BindingEditor,
    BindingList,
    EnforcementBadge,
  ],
  template: `
    <section class="mx-auto w-full max-w-6xl space-y-6 p-6 lg:p-8">
      <a
        routerLink="/contracts"
        class="text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-sm transition-colors"
      >
        <cd-icon name="chevron-left" size="0.875rem" />
        Contracts
      </a>

      @if (store.loading()) {
        <div class="space-y-4" aria-busy="true" aria-live="polite">
          <div hlmSkeleton class="h-9 w-64"></div>
          <div hlmSkeleton class="h-24 w-full rounded-xl"></div>
        </div>
      } @else if (store.contract(); as contract) {
        <header class="flex flex-wrap items-center gap-3">
          <h1 class="text-foreground font-mono text-2xl font-bold tracking-tight">
            {{ contract.name }}
          </h1>
          <cd-enforcement-badge [mode]="contract.enforcement" />
        </header>

        <div role="status" aria-live="polite">
          @if (store.lastSaved(); as saved) {
            <div hlmAlert>
              <p hlmAlertDescription>{{ savedMessage(saved) }}</p>
            </div>
          }
        </div>

        <section
          *cdIfScope="writeScopes"
          class="border-border bg-card space-y-3 rounded-xl border p-5"
          aria-labelledby="enforcement-heading"
        >
          <h2 id="enforcement-heading" class="text-foreground text-sm font-semibold">
            Enforcement
          </h2>

          <div class="flex flex-wrap items-end gap-3">
            <div class="space-y-1.5">
              <label class="text-sm font-medium" for="enforcement-mode">Mode</label>
              <select id="enforcement-mode" [class]="input" (change)="choose($event)">
                @for (mode of modes; track mode) {
                  <option [value]="mode" [selected]="(chosen() ?? contract.enforcement) === mode">
                    {{ mode }}
                  </option>
                }
              </select>
            </div>

            @if (changing(); as target) {
              @if (confirming()) {
                <span class="text-foreground text-sm">
                  Every service that resolves one of these routes will refuse a message that
                  violates it. Enforce {{ contract.name }}?
                </span>
                <button
                  type="button"
                  hlmBtn
                  size="sm"
                  [disabled]="store.saving() !== null"
                  (click)="apply(target)"
                >
                  Confirm
                </button>
                <button type="button" hlmBtn variant="ghost" size="sm" (click)="cancel()">
                  Cancel
                </button>
              } @else {
                <button
                  type="button"
                  hlmBtn
                  size="sm"
                  [disabled]="store.saving() !== null"
                  (click)="request(target)"
                >
                  Change to {{ target }}
                </button>
              }
            }
          </div>

          <p class="text-muted-foreground text-xs">
            {{ meaning(chosen() ?? contract.enforcement) }}
          </p>

          @if (store.actionErrors().enforcement; as error) {
            <div hlmAlert variant="destructive">
              <h3 hlmAlertTitle>Could not change the mode</h3>
              <p hlmAlertDescription>{{ error.detail }}</p>
            </div>
          }
        </section>

        <div *cdIfScope="writeScopes">
          @if (active(); as work) {
            <cd-binding-editor
              [target]="work.target"
              [mode]="work.mode"
              [enforcement]="contract.enforcement"
              [saving]="store.saving() !== null"
              [error]="
                (work.mode === 'change' ? store.actionErrors().change : store.actionErrors().remove)
                  ?.detail ?? null
              "
              (changed)="applyChange(work.target, $event)"
              (removed)="applyRemoval(work.target)"
              (cancelled)="stopEditing()"
            />
          }
        </div>

        <cd-binding-list
          [publishes]="contract.publishes"
          [consumes]="contract.consumes"
          [editable]="canWrite()"
          [activeId]="editing()?.id ?? null"
          (changeRequested)="startEditing($event, 'change')"
          (removeRequested)="startEditing($event, 'remove')"
        />

        <div *cdIfScope="writeScopes" class="grid gap-6 lg:grid-cols-2">
          <form
            [formGroup]="publishForm"
            (ngSubmit)="addPublish()"
            class="border-border bg-card space-y-3 rounded-xl border p-5"
            aria-labelledby="add-publish-heading"
          >
            <h2 id="add-publish-heading" class="text-foreground text-sm font-semibold">
              Add a publish binding
            </h2>

            <div class="space-y-1.5">
              <label class="text-sm font-medium" for="publish-exchange">Exchange</label>
              <input
                id="publish-exchange"
                formControlName="exchange"
                autocomplete="off"
                placeholder="orders"
                [class]="input + ' font-mono'"
              />
            </div>

            <div class="space-y-1.5">
              <label class="text-sm font-medium" for="publish-pattern">Routing key pattern</label>
              <input
                id="publish-pattern"
                formControlName="pattern"
                autocomplete="off"
                placeholder="orders.*"
                [class]="input + ' font-mono'"
              />
              <p class="text-muted-foreground text-xs">
                An AMQP topic pattern: <code>*</code> is one word, <code>#</code> is any number.
              </p>
            </div>

            <div class="space-y-1.5">
              <label class="text-sm font-medium" for="publish-subjects">Subjects</label>
              <input
                id="publish-subjects"
                formControlName="subjects"
                autocomplete="off"
                placeholder="orders.created@>=1"
                [class]="input + ' font-mono'"
              />
              <p class="text-muted-foreground text-xs">{{ subjectsHelp }}</p>
            </div>

            <div class="grid grid-cols-2 gap-3">
              <div class="space-y-1.5">
                <label class="text-sm font-medium" for="publish-vhost">
                  Virtual host <span class="text-muted-foreground font-normal">· optional</span>
                </label>
                <input
                  id="publish-vhost"
                  formControlName="virtualHost"
                  placeholder="/"
                  [class]="input + ' font-mono'"
                />
              </div>
              <div class="space-y-1.5">
                <label class="text-sm font-medium" for="publish-precedence">
                  Precedence <span class="text-muted-foreground font-normal">· optional</span>
                </label>
                <input
                  id="publish-precedence"
                  type="number"
                  formControlName="precedence"
                  [class]="input"
                />
              </div>
            </div>
            <p class="text-muted-foreground text-xs">
              Precedence decides which binding wins when two overlap, and it only separates them
              when both have a value and the values differ. It can be changed afterwards, from the
              binding's own Change button.
            </p>

            @if (publishError(); as message) {
              <p class="text-destructive text-sm" role="alert">{{ message }}</p>
            }
            @if (store.actionErrors().publish; as error) {
              <div hlmAlert variant="destructive">
                <h3 hlmAlertTitle>Could not add the binding</h3>
                <p hlmAlertDescription>{{ error.detail }}</p>
              </div>
            }

            <button type="submit" hlmBtn size="sm" [disabled]="store.saving() !== null">
              Add publish binding
            </button>
          </form>

          <form
            [formGroup]="consumeForm"
            (ngSubmit)="addConsume()"
            class="border-border bg-card space-y-3 rounded-xl border p-5"
            aria-labelledby="add-consume-heading"
          >
            <h2 id="add-consume-heading" class="text-foreground text-sm font-semibold">
              Add a consume binding
            </h2>

            <div class="space-y-1.5">
              <label class="text-sm font-medium" for="consume-queue">Queue</label>
              <input
                id="consume-queue"
                formControlName="queue"
                autocomplete="off"
                placeholder="billing.orders"
                [class]="input + ' font-mono'"
              />
            </div>

            <div class="space-y-1.5">
              <label class="text-sm font-medium" for="consume-subjects">Subjects</label>
              <input
                id="consume-subjects"
                formControlName="subjects"
                autocomplete="off"
                placeholder="orders.created"
                [class]="input + ' font-mono'"
              />
              <p class="text-muted-foreground text-xs">{{ subjectsHelp }}</p>
            </div>

            <div class="space-y-1.5">
              <label class="text-sm font-medium" for="consume-vhost">
                Virtual host <span class="text-muted-foreground font-normal">· optional</span>
              </label>
              <input
                id="consume-vhost"
                formControlName="virtualHost"
                placeholder="/"
                [class]="input + ' font-mono'"
              />
            </div>

            @if (consumeError(); as message) {
              <p class="text-destructive text-sm" role="alert">{{ message }}</p>
            }
            @if (store.actionErrors().consume; as error) {
              <div hlmAlert variant="destructive">
                <h3 hlmAlertTitle>Could not add the binding</h3>
                <p hlmAlertDescription>{{ error.detail }}</p>
              </div>
            }

            <button type="submit" hlmBtn size="sm" [disabled]="store.saving() !== null">
              Add consume binding
            </button>
          </form>
        </div>
      } @else if (store.error(); as error) {
        <div hlmAlert variant="destructive">
          <h2 hlmAlertTitle>Could not load this contract</h2>
          <p hlmAlertDescription>{{ error.detail }}</p>
          <button hlmBtn hlmAlertAction variant="outline" size="sm" (click)="reload()">
            Try again
          </button>
        </div>
      }
    </section>
  `,
})
export class ContractDetailPage {
  /** The contract name, bound from the route path. */
  readonly name = input.required<string>();

  protected readonly store = inject(ContractDetailStore);
  protected readonly environments = inject(ActiveEnvironmentStore);

  protected readonly input = INPUT;
  protected readonly modes = ENFORCEMENT_MODES;
  protected readonly writeScopes = CONTRACT_WRITE_SCOPES;
  protected readonly subjectsHelp = SUBJECTS_HELP;

  private readonly session = inject(SessionStore);

  /** Whether the caller may change a contract (the same rule as the `cdIfScope` guards). */
  protected readonly canWrite = computed(
    () => this.session.claimed() === false || grants(this.session.scopes(), CONTRACT_WRITE_SCOPES),
  );

  /** The binding being changed or removed, by kind and id, or null. */
  protected readonly editing = signal<{
    readonly kind: 'publish' | 'consume';
    readonly id: string;
    readonly mode: 'change' | 'remove';
  } | null>(null);

  /**
   * The binding being worked on, read from the contract on screen rather than kept as a copy, so
   * what the editor shows is always what the registry last said (and nothing when it is gone).
   */
  protected readonly active = computed(() => {
    const work = this.editing();
    const contract = this.store.contract();

    if (work === null || contract === null) {
      return null;
    }

    const target: BindingTarget | null =
      work.kind === 'publish'
        ? ((binding) => (binding ? { kind: 'publish' as const, binding } : null))(
            contract.publishes.find((b) => b.id === work.id),
          )
        : ((binding) => (binding ? { kind: 'consume' as const, binding } : null))(
            contract.consumes.find((b) => b.id === work.id),
          );

    return target === null ? null : { target, mode: work.mode };
  });

  /** The mode picked in the selector but not yet applied, or null when it still shows the contract's own. */
  protected readonly chosen = signal<EnforcementMode | null>(null);

  /** Whether the pending change to `ENFORCE` has been asked for once and awaits its second click. */
  protected readonly confirming = signal(false);

  /** Set by a client-side check, apart from anything the registry says. */
  protected readonly publishError = signal<string | null>(null);
  protected readonly consumeError = signal<string | null>(null);

  protected readonly publishForm = inject(FormBuilder).nonNullable.group({
    exchange: ['', Validators.required],
    pattern: ['', Validators.required],
    subjects: ['', Validators.required],
    virtualHost: [''],
    precedence: [null as number | null],
  });

  protected readonly consumeForm = inject(FormBuilder).nonNullable.group({
    queue: ['', Validators.required],
    subjects: ['', Validators.required],
    virtualHost: [''],
  });

  constructor() {
    effect(() => {
      this.environments.name();
      this.store.load(this.name());
    });

    // A stored binding empties its own form, so a second click cannot send it again and be
    // refused as the overlap with itself. Only on success: a refusal keeps what was typed,
    // because the next move is to edit it.
    effect(() => {
      const saved = this.store.lastSaved();

      if (saved === 'publish') {
        this.publishForm.reset();
      }

      if (saved === 'consume') {
        this.consumeForm.reset();
      }

      if (saved === 'enforcement') {
        this.chosen.set(null);
        this.confirming.set(false);
      }

      // A stored change or removal closes its own panel. A refusal keeps it open, with the
      // registry's words in it, because the next move is to edit what was typed.
      if (saved === 'change' || saved === 'remove') {
        this.editing.set(null);
      }
    });
  }

  protected startEditing(binding: BindingRef, mode: 'change' | 'remove'): void {
    this.editing.set({ ...binding, mode });
  }

  protected stopEditing(): void {
    this.editing.set(null);
  }

  protected applyChange(target: BindingTarget, change: BindingChange): void {
    if (target.kind === 'publish') {
      this.store.replacePublishBinding({
        id: target.binding.id,
        subjects: change.subjects,
        precedence: change.precedence,
      });
    } else {
      this.store.replaceConsumeBinding({ id: target.binding.id, subjects: change.subjects });
    }
  }

  protected applyRemoval(target: BindingTarget): void {
    if (target.kind === 'publish') {
      this.store.removePublishBinding(target.binding.id);
    } else {
      this.store.removeConsumeBinding(target.binding.id);
    }
  }

  /** The mode the selector is on, if it differs from the contract's own. */
  protected changing(): EnforcementMode | null {
    const picked = this.chosen();
    const current = this.store.contract()?.enforcement;

    return picked !== null && picked !== current ? picked : null;
  }

  protected meaning(mode: EnforcementMode): string {
    return ENFORCEMENT_MEANING[mode];
  }

  protected choose(event: Event): void {
    this.chosen.set((event.target as HTMLSelectElement).value as EnforcementMode);
    this.confirming.set(false);
  }

  /** The first click. Anything but `ENFORCE` is the safe direction and applies at once. */
  protected request(target: EnforcementMode): void {
    if (target === 'ENFORCE') {
      this.confirming.set(true);
      return;
    }

    this.apply(target);
  }

  protected apply(target: EnforcementMode): void {
    this.confirming.set(false);
    this.store.setEnforcement(target);
  }

  protected cancel(): void {
    this.confirming.set(false);
    this.chosen.set(null);
  }

  protected addPublish(): void {
    this.publishError.set(null);

    if (this.publishForm.invalid) {
      this.publishError.set(
        'The exchange, the routing key pattern and a subject are all required.',
      );
      return;
    }

    const value = this.publishForm.getRawValue();
    const subjects = parseSubjectRefs(value.subjects);

    if (subjects === null) {
      this.publishError.set(subjectsRefusal(value.subjects));
      return;
    }

    this.store.addPublishBinding({
      exchange: value.exchange.trim(),
      routingKeyPattern: value.pattern.trim(),
      subjects,
      virtualHost: value.virtualHost,
      precedence: value.precedence,
    });
  }

  protected addConsume(): void {
    this.consumeError.set(null);

    if (this.consumeForm.invalid) {
      this.consumeError.set('The queue and a subject are both required.');
      return;
    }

    const value = this.consumeForm.getRawValue();
    const subjects = parseSubjectRefs(value.subjects);

    if (subjects === null) {
      this.consumeError.set(subjectsRefusal(value.subjects));
      return;
    }

    this.store.addConsumeBinding({
      queue: value.queue.trim(),
      subjects,
      virtualHost: value.virtualHost,
    });
  }

  protected savedMessage(action: ContractAction): string {
    switch (action) {
      case 'enforcement':
        return `Enforcement is now ${this.store.contract()?.enforcement}.`;
      case 'publish':
        return 'Publish binding added.';
      case 'consume':
        return 'Consume binding added.';
      case 'change':
        return 'Binding changed.';
      case 'remove':
        return 'Binding removed.';
    }
  }

  protected reload(): void {
    this.store.load(this.name());
  }
}
