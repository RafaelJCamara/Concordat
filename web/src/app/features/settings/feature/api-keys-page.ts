import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { SessionStore } from '../../../core/auth/session-store';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import type { Scope } from '../../../domain/identity/scope';
import { SCOPES } from '../../../domain/identity/scope';
import { RelativeTimePipe } from '../../../shared/pipes/relative-time-pipe';
import { OneTimeSecret } from '../../../shared/ui/one-time-secret/one-time-secret';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';
import { ApiKeysStore } from '../application/api-keys-store';
import { SettingsNav } from './settings-nav';

const INPUT =
  'border-input bg-background focus-visible:ring-ring flex h-9 w-full rounded-md border px-3 py-1 text-sm focus-visible:ring-1 focus-visible:outline-none';

/**
 * The organisation's API keys.
 *
 * <b>The secret is shown once, in a panel that does not go away until it is dismissed.</b> The
 * registry stores only a hash and cannot show a key again, so the response to issuing one is the
 * only moment anybody can copy it. The panel says so, offers the copy, and stays — a toast would
 * take the one copy with it. Dismissing it clears the secret from memory; it was never written
 * to the list, to storage, or to a URL.
 *
 * <b>Only scopes the issuer holds are offered.</b> A key may not grant more than its issuer
 * holds — the server refuses it with 403 otherwise — so offering the rest would be a list of
 * things certain to fail. The list is the session's own scopes, which the API returned at
 * sign-in.
 *
 * <b>Scopes are not pre-ticked.</b> A key is least-privilege by construction only if somebody
 * chose each scope; a form that opened with them all ticked would make the dangerous key the
 * one that takes no effort.
 *
 * <b>Revocation takes two clicks and names the key.</b> Whatever holds it stops working on its
 * next request, and nothing about that can be taken back.
 */
@Component({
  selector: 'cd-api-keys-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [ApiKeysStore],
  imports: [
    ReactiveFormsModule,
    HlmAlertImports,
    HlmButton,
    HlmSkeleton,
    OneTimeSecret,
    RelativeTimePipe,
    SettingsNav,
    StatusBadge,
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

      @if (store.issued(); as issued) {
        <cd-one-time-secret
          heading="Copy this key now — it will not be shown again"
          label="API key secret"
          [secret]="issued.secret"
          (dismissed)="store.dismiss()"
        >
          The registry keeps only a hash of
          <span class="text-foreground font-medium">{{ issued.key.label }}</span
          >, so it cannot show the key a second time. If it is lost, revoke it and issue another.
        </cd-one-time-secret>
      }

      @if (store.error(); as error) {
        <div hlmAlert variant="destructive">
          <h2 hlmAlertTitle>Could not load API keys</h2>
          <p hlmAlertDescription>{{ error.detail }}</p>
          <button hlmBtn hlmAlertAction variant="outline" size="sm" (click)="store.load()">
            Try again
          </button>
        </div>
      } @else if (store.loading()) {
        <div class="space-y-2" aria-busy="true" aria-live="polite">
          <div hlmSkeleton class="h-16 w-full rounded-xl"></div>
        </div>
      } @else {
        @if (store.isEmpty()) {
          <div class="border-border bg-card rounded-xl border p-10 text-center">
            <p class="font-medium">No API keys</p>
            <p class="text-muted-foreground mx-auto mt-1 max-w-xl text-sm">
              Pipelines and services authenticate with a key. People sign in with a password.
            </p>
          </div>
        } @else {
          <ul class="space-y-2">
            @for (key of store.keys(); track key.id) {
              <li class="border-border bg-card rounded-xl border p-4">
                <div class="flex flex-wrap items-center gap-2">
                  <span class="text-foreground font-medium">{{ key.label }}</span>
                  <span class="text-muted-foreground font-mono text-xs">{{ key.keyId }}</span>
                  @if (key.revokedAt) {
                    <cd-status-badge tone="destructive">REVOKED</cd-status-badge>
                  } @else if (isExpired(key.expiresAt)) {
                    <cd-status-badge tone="neutral">EXPIRED</cd-status-badge>
                  }
                </div>

                <div class="mt-2 flex flex-wrap gap-1.5">
                  @for (scope of key.scopes; track scope) {
                    <span
                      class="bg-muted text-muted-foreground rounded px-1.5 py-0.5 font-mono text-xs"
                    >
                      {{ scope }}
                    </span>
                  }
                </div>

                <p class="text-muted-foreground mt-2 text-xs">
                  Created
                  <span [title]="key.createdAt.toISOString()">{{
                    key.createdAt | cdRelativeTime
                  }}</span>
                  ·
                  @if (key.lastUsedAt; as used) {
                    used <span [title]="used.toISOString()">{{ used | cdRelativeTime }}</span>
                  } @else {
                    never used
                  }
                  ·
                  @if (key.expiresAt; as expires) {
                    expires
                    <span [title]="expires.toISOString()">{{ expires | cdRelativeTime }}</span>
                  } @else {
                    does not expire
                  }
                </p>

                @if (!key.revokedAt) {
                  <div class="mt-3 flex flex-wrap items-center gap-2">
                    @if (confirming() === key.id) {
                      <span class="text-foreground text-sm">
                        Revoke {{ key.label }}? Whatever holds it stops working on its next request.
                      </span>
                      <button
                        type="button"
                        hlmBtn
                        variant="destructive"
                        size="sm"
                        [disabled]="store.revoking().includes(key.id)"
                        (click)="revoke(key.id)"
                      >
                        Confirm
                      </button>
                      <button
                        type="button"
                        hlmBtn
                        variant="ghost"
                        size="sm"
                        (click)="confirming.set(null)"
                      >
                        Cancel
                      </button>
                    } @else {
                      <button
                        type="button"
                        hlmBtn
                        variant="ghost"
                        size="sm"
                        (click)="confirming.set(key.id)"
                      >
                        Revoke
                      </button>
                    }
                  </div>
                }

                @if (store.revokeErrors()[key.id]; as error) {
                  <div hlmAlert variant="destructive" class="mt-3">
                    <h3 hlmAlertTitle>Could not revoke the key</h3>
                    <p hlmAlertDescription>{{ error.detail }}</p>
                  </div>
                }
              </li>
            }
          </ul>
        }

        <form
          [formGroup]="form"
          (ngSubmit)="issue()"
          class="border-border bg-card space-y-3 rounded-xl border p-5"
          aria-labelledby="issue-heading"
        >
          <h2 id="issue-heading" class="text-foreground text-sm font-semibold">Issue a key</h2>

          <div class="grid gap-3 sm:grid-cols-2">
            <div class="space-y-1.5">
              <label class="text-sm font-medium" for="key-label">Label</label>
              <input
                id="key-label"
                formControlName="label"
                autocomplete="off"
                placeholder="ci-pipeline"
                [class]="input"
              />
              <p class="text-muted-foreground text-xs">
                What it is for. An unlabelled key is one nobody dares revoke.
              </p>
            </div>
            <div class="space-y-1.5">
              <label class="text-sm font-medium" for="key-expires">
                Expires <span class="text-muted-foreground font-normal">· optional</span>
              </label>
              <input id="key-expires" type="date" formControlName="expires" [class]="input" />
            </div>
          </div>

          <fieldset class="space-y-1.5">
            <legend class="text-sm font-medium">Scopes</legend>
            <div class="flex flex-wrap gap-x-4 gap-y-1.5">
              @for (scope of offered(); track scope) {
                <label class="flex items-center gap-1.5 font-mono text-xs">
                  <input
                    type="checkbox"
                    [checked]="chosen().has(scope)"
                    (change)="toggle(scope)"
                    class="size-3.5"
                  />
                  {{ scope }}
                </label>
              }
            </div>
            <p class="text-muted-foreground text-xs">
              A key cannot hold more than you do. None are ticked: choose each one it needs.
            </p>
          </fieldset>

          @if (store.issueError(); as error) {
            <div hlmAlert variant="destructive">
              <h3 hlmAlertTitle>Could not issue the key</h3>
              <p hlmAlertDescription>{{ error.detail }}</p>
            </div>
          }

          <button
            type="submit"
            hlmBtn
            size="sm"
            [disabled]="form.invalid || chosen().size === 0 || store.issuing()"
          >
            Issue key
          </button>
        </form>
      }
    </section>
  `,
})
export class ApiKeysPage {
  protected readonly store = inject(ApiKeysStore);
  protected readonly environments = inject(ActiveEnvironmentStore);
  private readonly session = inject(SessionStore);

  protected readonly input = INPUT;

  /** The scopes this caller may hand on: their own, in the registry's order. */
  protected readonly offered = () =>
    SCOPES.filter((scope) => this.session.scopes().includes(scope));

  protected readonly chosen = signal<ReadonlySet<Scope>>(new Set());
  protected readonly confirming = signal<string | null>(null);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    label: ['', [Validators.required, Validators.pattern(/\S/)]],
    expires: [''],
  });

  constructor() {
    effect(() => {
      this.store.load();
    });

    // An issued key empties the form, so a second click cannot ask for a second secret nobody
    // meant to create.
    effect(() => {
      if (this.store.issued() !== null) {
        this.form.reset();
        this.chosen.set(new Set());
      }
    });
  }

  protected toggle(scope: Scope): void {
    const next = new Set(this.chosen());

    if (!next.delete(scope)) {
      next.add(scope);
    }

    this.chosen.set(next);
  }

  protected issue(): void {
    if (this.form.invalid || this.chosen().size === 0) {
      return;
    }

    const { label, expires } = this.form.getRawValue();

    this.store.issue({
      label: label.trim(),
      // In the registry's own order, not the order they were ticked, so the same choice always
      // produces the same request.
      scopes: SCOPES.filter((scope) => this.chosen().has(scope)),
      // The end of that local day: "expires on the 5th" should mean it works through the 5th.
      expiresAt: expires === '' ? null : new Date(`${expires}T23:59:59.999`),
    });
  }

  protected revoke(id: string): void {
    this.confirming.set(null);
    this.store.revoke(id);
  }

  protected isExpired(expiresAt: Date | null): boolean {
    return expiresAt !== null && expiresAt.getTime() < Date.now();
  }
}
