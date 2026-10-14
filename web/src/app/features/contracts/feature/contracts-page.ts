import { ChangeDetectionStrategy, Component, effect, inject } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { IfScope } from '../../../core/auth/if-scope';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { ENFORCEMENT_MODES, type EnforcementMode } from '../../../domain/contracts/contract';
import { CONTRACT_WRITE_SCOPES } from '../../../domain/identity/scope';
import { RelativeTimePipe } from '../../../shared/pipes/relative-time-pipe';
import { ContractListStore } from '../application/contract-list-store';
import { EnforcementBadge } from '../../../shared/ui/enforcement-badge/enforcement-badge';

const INPUT =
  'border-input bg-background focus-visible:ring-ring flex h-9 w-full rounded-md border px-3 py-1 text-sm focus-visible:ring-1 focus-visible:outline-none';

/**
 * The environment's contracts.
 *
 * <b>Creation is offered only to someone who can do it, and is absent otherwise</b> (ADR-018).
 * The form lets the registry choose the enforcement mode when left on "default", because the
 * registry's default — `MONITOR` — is a decision it owns: a contract authored by guessing about
 * a live topology must not start refusing traffic the moment it is written, and a screen that
 * picked `ENFORCE` for convenience would undo that.
 */
@Component({
  selector: 'cd-contracts-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [ContractListStore],
  imports: [
    ReactiveFormsModule,
    RouterLink,
    HlmAlertImports,
    HlmButton,
    HlmSkeleton,
    IfScope,
    EnforcementBadge,
    RelativeTimePipe,
  ],
  template: `
    <section class="mx-auto w-full max-w-6xl space-y-6 p-6 lg:p-8">
      <header>
        <h1 class="text-foreground text-2xl font-bold tracking-tight">Contracts</h1>
        <p class="text-muted-foreground mt-1">
          Which subjects may travel on which routes in
          <span class="font-mono">{{ environments.name() }}</span>
        </p>
      </header>

      <form
        *cdIfScope="writeScopes"
        [formGroup]="form"
        (ngSubmit)="create()"
        class="border-border bg-card flex flex-wrap items-end gap-4 rounded-xl border p-5"
      >
        <div class="min-w-48 flex-1 space-y-1.5">
          <label class="text-sm font-medium" for="contract-name">New contract</label>
          <input
            id="contract-name"
            formControlName="name"
            placeholder="payments"
            autocomplete="off"
            [class]="input + ' font-mono'"
          />
        </div>

        <div class="space-y-1.5">
          <label class="text-sm font-medium" for="contract-enforcement">Enforcement</label>
          <select id="contract-enforcement" formControlName="enforcement" [class]="input">
            <option value="">Default (MONITOR)</option>
            @for (mode of modes; track mode) {
              <option [value]="mode">{{ mode }}</option>
            }
          </select>
        </div>

        <button type="submit" hlmBtn [disabled]="store.creating() || form.controls.name.invalid">
          Create
        </button>

        @if (store.createError(); as error) {
          <div hlmAlert variant="destructive" class="w-full">
            <h2 hlmAlertTitle>Could not create the contract</h2>
            <p hlmAlertDescription>{{ error.detail }}</p>
          </div>
        }
      </form>

      <div role="status" aria-live="polite">
        @if (store.created(); as created) {
          <div hlmAlert>
            <p hlmAlertDescription>
              Created
              <a
                class="text-primary font-mono underline"
                [routerLink]="['/contracts', created.name]"
                >{{ created.name }}</a
              >
              as {{ created.enforcement }}. It governs nothing until it has a binding.
            </p>
          </div>
        }
      </div>

      @if (store.error(); as error) {
        <div hlmAlert variant="destructive">
          <h2 hlmAlertTitle>Could not load contracts</h2>
          <p hlmAlertDescription>{{ error.detail }}</p>
          <button hlmBtn hlmAlertAction variant="outline" size="sm" (click)="store.load()">
            Try again
          </button>
        </div>
      } @else if (store.loading()) {
        <div class="space-y-2" aria-busy="true" aria-live="polite">
          <div hlmSkeleton class="h-16 w-full rounded-xl"></div>
          <div hlmSkeleton class="h-16 w-full rounded-xl"></div>
        </div>
      } @else if (store.isEmpty()) {
        <div class="border-border bg-card rounded-xl border p-10 text-center">
          <p class="font-medium">No contracts yet</p>
          <p class="text-muted-foreground mx-auto mt-1 max-w-xl text-sm">
            Until a contract binds a route to subjects, nothing is governed and a service resolves
            every route as ungoverned and unenforced.
          </p>
        </div>
      } @else {
        <ul class="space-y-3">
          @for (contract of store.contracts(); track contract.name) {
            <li class="border-border bg-card rounded-xl border p-4">
              <div class="flex flex-wrap items-center gap-3">
                <a
                  class="text-foreground hover:text-primary font-mono text-lg font-semibold transition-colors"
                  [routerLink]="['/contracts', contract.name]"
                >
                  {{ contract.name }}
                </a>
                <cd-enforcement-badge [mode]="contract.enforcement" />
                <span class="text-muted-foreground ms-auto text-sm">
                  {{ contract.publishes.length }} publish · {{ contract.consumes.length }} consume ·
                  <span [title]="contract.createdAt.toISOString()">
                    created {{ contract.createdAt | cdRelativeTime }}
                  </span>
                </span>
              </div>
            </li>
          }
        </ul>
      }
    </section>
  `,
})
export class ContractsPage {
  protected readonly store = inject(ContractListStore);
  protected readonly environments = inject(ActiveEnvironmentStore);

  protected readonly input = INPUT;
  protected readonly modes = ENFORCEMENT_MODES;
  protected readonly writeScopes = CONTRACT_WRITE_SCOPES;

  protected readonly form = inject(FormBuilder).nonNullable.group({
    name: ['', [Validators.required, Validators.pattern(/\S/)]],
    enforcement: [''],
  });

  constructor() {
    effect(() => {
      this.environments.name();
      this.store.load();
    });

    // A created contract empties the form, so a second click cannot send the same name again
    // and be refused as a duplicate of something that was in fact created.
    effect(() => {
      if (this.store.created() !== null) {
        this.form.reset();
      }
    });
  }

  protected create(): void {
    const { name, enforcement } = this.form.getRawValue();

    if (this.form.invalid) {
      return;
    }

    this.store.create({
      name: name.trim(),
      enforcement: enforcement === '' ? null : (enforcement as EnforcementMode),
    });
  }
}
