import { ChangeDetectionStrategy, Component, effect, inject } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { IfScope } from '../../../core/auth/if-scope';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { BROKER_WRITE_SCOPES } from '../../../domain/identity/scope';
import { EnvironmentStore } from '../application/environment-store';
import { BrokerRow } from './broker-row';
import { SettingsNav } from './settings-nav';

const INPUT =
  'border-input bg-background focus-visible:ring-ring flex h-9 w-full rounded-md border px-3 py-1 text-sm focus-visible:ring-1 focus-visible:outline-none';

/**
 * The brokers registered in this environment.
 *
 * <b>A broker's identity is its host, port and virtual host together</b>, so the same host may
 * appear more than once under different virtual hosts, and display names must be unique
 * because they are how an operator tells entries apart. The registry enforces both; this screen
 * renders whichever refusal it gives, under the form that caused it.
 *
 * <b>An unreachable broker is shown, never an error.</b> A connection check that finds the
 * broker down is the registry doing its job — it records the result and returns it — and a
 * screen that reported it as a failure would blame the registry for somebody else's outage.
 */
@Component({
  selector: 'cd-brokers-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [EnvironmentStore],
  imports: [
    ReactiveFormsModule,
    HlmAlertImports,
    HlmButton,
    HlmSkeleton,
    IfScope,
    BrokerRow,
    SettingsNav,
  ],
  template: `
    <section class="mx-auto w-full max-w-4xl space-y-6 p-6 lg:p-8">
      <header>
        <h1 class="text-foreground text-2xl font-bold tracking-tight">Settings</h1>
        <p class="text-muted-foreground mt-1">
          <span class="font-mono">{{ environments.name() }}</span>
        </p>
      </header>

      <cd-settings-nav />

      <div role="status" aria-live="polite">
        @if (store.lastSaved() === 'add-broker') {
          <div hlmAlert>
            <p hlmAlertDescription>Broker added. Check the connection to see whether it answers.</p>
          </div>
        }
      </div>

      @if (store.loading()) {
        <div class="space-y-3" aria-busy="true" aria-live="polite">
          <div hlmSkeleton class="h-32 w-full rounded-xl"></div>
        </div>
      } @else if (store.environment(); as environment) {
        @if (environment.brokers.length === 0) {
          <div class="border-border bg-card rounded-xl border p-10 text-center">
            <p class="font-medium">No brokers registered</p>
            <p class="text-muted-foreground mx-auto mt-1 max-w-xl text-sm">
              A broker here is a connection the registry can test and a binding can name. Nothing is
              wrong without one — services resolve contracts against the registry, not through it.
            </p>
          </div>
        } @else {
          <ul class="space-y-4">
            @for (broker of environment.brokers; track broker.brokerId) {
              <li><cd-broker-row [broker]="broker" /></li>
            }
          </ul>
        }

        <form
          *cdIfScope="writeScopes"
          [formGroup]="form"
          (ngSubmit)="add()"
          class="border-border bg-card space-y-3 rounded-xl border p-5"
          aria-labelledby="add-broker-heading"
        >
          <h2 id="add-broker-heading" class="text-foreground text-sm font-semibold">
            Add a broker
          </h2>

          <div class="grid gap-3 sm:grid-cols-2">
            <div class="space-y-1.5">
              <label class="text-sm font-medium" for="broker-name">Display name</label>
              <input
                id="broker-name"
                formControlName="displayName"
                autocomplete="off"
                placeholder="primary"
                [class]="input"
              />
            </div>
            <div class="space-y-1.5">
              <label class="text-sm font-medium" for="broker-vhost">
                Virtual host <span class="text-muted-foreground font-normal">· optional</span>
              </label>
              <input
                id="broker-vhost"
                formControlName="virtualHost"
                placeholder="/"
                [class]="input + ' font-mono'"
              />
            </div>
          </div>

          <div class="space-y-1.5">
            <label class="text-sm font-medium" for="broker-uri">Endpoint</label>
            <input
              id="broker-uri"
              formControlName="uri"
              autocomplete="off"
              placeholder="amqps://rabbit.example.com:5671"
              [class]="input + ' font-mono'"
            />
            <p class="text-muted-foreground text-xs">
              An <code>amqp</code> or <code>amqps</code> address. TLS follows the scheme.
            </p>
          </div>

          @if (store.actionErrors()['add-broker']; as error) {
            <div hlmAlert variant="destructive">
              <h3 hlmAlertTitle>Could not add the broker</h3>
              <p hlmAlertDescription>{{ error.detail }}</p>
            </div>
          }

          <button
            type="submit"
            hlmBtn
            size="sm"
            [disabled]="form.invalid || store.saving() !== null"
          >
            Add broker
          </button>
        </form>
      } @else if (store.error(); as error) {
        <div hlmAlert variant="destructive">
          <h2 hlmAlertTitle>Could not load this environment</h2>
          <p hlmAlertDescription>{{ error.detail }}</p>
          <button hlmBtn hlmAlertAction variant="outline" size="sm" (click)="store.load()">
            Try again
          </button>
        </div>
      }
    </section>
  `,
})
export class BrokersPage {
  protected readonly store = inject(EnvironmentStore);
  protected readonly environments = inject(ActiveEnvironmentStore);

  protected readonly input = INPUT;
  protected readonly writeScopes = BROKER_WRITE_SCOPES;

  protected readonly form = inject(FormBuilder).nonNullable.group({
    displayName: ['', [Validators.required, Validators.pattern(/\S/)]],
    uri: ['', [Validators.required, Validators.pattern(/\S/)]],
    virtualHost: [''],
  });

  constructor() {
    effect(() => {
      this.environments.name();
      this.store.load();
    });

    // A stored broker empties the form, so a second click cannot send it again and be refused
    // as a duplicate of itself. Only on success: a refusal keeps what was typed.
    effect(() => {
      if (this.store.lastSaved() === 'add-broker') {
        this.form.reset();
      }
    });
  }

  protected add(): void {
    if (this.form.invalid) {
      return;
    }

    const value = this.form.getRawValue();

    this.store.addBroker({
      displayName: value.displayName.trim(),
      uri: value.uri.trim(),
      virtualHost: value.virtualHost,
    });
  }
}
