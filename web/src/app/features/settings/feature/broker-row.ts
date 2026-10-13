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
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { IfScope } from '../../../core/auth/if-scope';
import { BROKER_WRITE_SCOPES } from '../../../domain/identity/scope';
import type { Broker, BrokerStatus } from '../../../domain/environments/environment';
import { RelativeTimePipe } from '../../../shared/pipes/relative-time-pipe';
import { StatusBadge, type StatusTone } from '../../../shared/ui/status-badge/status-badge';
import { EnvironmentStore } from '../application/environment-store';

const INPUT =
  'border-input bg-background focus-visible:ring-ring flex h-9 w-full rounded-md border px-3 py-1 text-sm focus-visible:ring-1 focus-visible:outline-none';

const TONES: Record<BrokerStatus, StatusTone> = {
  REACHABLE: 'success',
  UNREACHABLE: 'destructive',
  UNKNOWN: 'neutral',
};

/** What each destructive action asks, before it is sent. */
type Pending = 'remove-credentials' | 'remove' | null;

/**
 * One registered broker, and what can be done to it.
 *
 * <b>The credential form never shows a credential.</b> They are write-only over the API
 * (ADR-012): a read reports only whether one exists, so this renders "stored" or "none" and a
 * pair of empty fields to replace it with. The password field is `type=password` with
 * `autocomplete="new-password"` — without that a password manager offers to fill the broker's
 * password with the one the browser has saved for the registry itself, which is somebody's
 * sign-in being written into a broker.
 *
 * <b>The form empties itself after a successful save</b>, so the password does not sit in the
 * DOM, in the field, for the next person at the screen. A refusal keeps what was typed apart
 * from the password, which is cleared either way: if the registry refused the pair, the next
 * move is to type the password again, not to edit it in place.
 *
 * <b>Removing takes two clicks and names what goes.</b> Removing a broker takes its binding
 * scope with it, and removing its credentials means the next connection check fails.
 *
 * Not routed, but in `feature/`, because it owns UI state per broker and reads the store its
 * page provides — `ui/` may not inject one.
 */
@Component({
  selector: 'cd-broker-row',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    HlmAlertImports,
    HlmButton,
    IfScope,
    RelativeTimePipe,
    StatusBadge,
  ],
  host: { class: 'block' },
  template: `
    @let b = broker();
    <div class="border-border bg-card rounded-xl border p-5">
      <div class="flex flex-wrap items-center gap-2">
        <h2 class="text-foreground text-lg font-semibold">{{ b.displayName }}</h2>
        <cd-status-badge [tone]="tone()">{{ b.status }}</cd-status-badge>
        @if (b.useTls) {
          <cd-status-badge tone="info">TLS</cd-status-badge>
        }
        <cd-status-badge [tone]="b.hasCredentials ? 'success' : 'warning'">
          {{ b.hasCredentials ? 'credentials stored' : 'no credentials' }}
        </cd-status-badge>
      </div>

      <p class="text-muted-foreground mt-1 font-mono text-sm break-all">
        {{ b.uri }} · vhost {{ b.virtualHost }}
      </p>

      <p class="text-muted-foreground mt-1 text-xs">
        @if (b.lastCheckedAt; as checked) {
          Checked <span [title]="checked.toISOString()">{{ checked | cdRelativeTime }}</span>
        } @else {
          Never checked
        }
      </p>

      @if (b.lastError; as lastError) {
        <p class="text-destructive mt-1 text-sm">{{ lastError }}</p>
      }

      <div class="mt-4 flex flex-wrap items-center gap-2">
        <button
          type="button"
          hlmBtn
          variant="outline"
          size="sm"
          [disabled]="store.saving() !== null"
          (click)="store.checkBroker(b.brokerId)"
        >
          {{ store.saving() === 'check:' + b.brokerId ? 'Checking…' : 'Check connection' }}
        </button>

        <ng-container *cdIfScope="writeScopes">
          <button
            type="button"
            hlmBtn
            variant="outline"
            size="sm"
            [attr.aria-expanded]="editing()"
            (click)="editing.set(!editing())"
          >
            {{ b.hasCredentials ? 'Replace credentials' : 'Set credentials' }}
          </button>

          @if (pending() === 'remove-credentials') {
            <span class="text-foreground text-sm">
              Remove the stored credentials? The next check will fail.
            </span>
            <button
              type="button"
              hlmBtn
              variant="destructive"
              size="sm"
              [disabled]="store.saving() !== null"
              (click)="confirm()"
            >
              Confirm
            </button>
            <button type="button" hlmBtn variant="ghost" size="sm" (click)="pending.set(null)">
              Cancel
            </button>
          } @else if (pending() === 'remove') {
            <span class="text-foreground text-sm">
              Remove {{ b.displayName }}? Bindings that name it stop applying.
            </span>
            <button
              type="button"
              hlmBtn
              variant="destructive"
              size="sm"
              [disabled]="store.saving() !== null"
              (click)="confirm()"
            >
              Confirm
            </button>
            <button type="button" hlmBtn variant="ghost" size="sm" (click)="pending.set(null)">
              Cancel
            </button>
          } @else {
            @if (b.hasCredentials) {
              <button
                type="button"
                hlmBtn
                variant="ghost"
                size="sm"
                (click)="pending.set('remove-credentials')"
              >
                Remove credentials
              </button>
            }
            <button type="button" hlmBtn variant="ghost" size="sm" (click)="pending.set('remove')">
              Remove broker
            </button>
          }
        </ng-container>
      </div>

      @for (error of errors(); track error.action) {
        <div hlmAlert variant="destructive" class="mt-4">
          <h3 hlmAlertTitle>{{ error.title }}</h3>
          <p hlmAlertDescription>{{ error.detail }}</p>
        </div>
      }

      @if (editing()) {
        <form
          *cdIfScope="writeScopes"
          [formGroup]="form"
          (ngSubmit)="save()"
          class="border-border mt-4 space-y-3 border-t pt-4"
          [attr.aria-label]="'Credentials for ' + b.displayName"
        >
          <p class="text-muted-foreground text-xs">
            Write-only: the registry never returns a credential, so these fields are always empty.
            Saving replaces whatever is stored.
          </p>
          <div class="grid gap-3 sm:grid-cols-2">
            <div class="space-y-1.5">
              <label class="text-sm font-medium" [for]="'user-' + b.brokerId">Username</label>
              <input
                [id]="'user-' + b.brokerId"
                formControlName="username"
                autocomplete="off"
                [class]="input"
              />
            </div>
            <div class="space-y-1.5">
              <label class="text-sm font-medium" [for]="'pass-' + b.brokerId">Password</label>
              <input
                [id]="'pass-' + b.brokerId"
                type="password"
                formControlName="password"
                autocomplete="new-password"
                [class]="input"
              />
            </div>
          </div>
          <button
            type="submit"
            hlmBtn
            size="sm"
            [disabled]="form.invalid || store.saving() !== null"
          >
            Save credentials
          </button>
        </form>
      }
    </div>
  `,
})
export class BrokerRow {
  readonly broker = input.required<Broker>();

  protected readonly store = inject(EnvironmentStore);

  protected readonly input = INPUT;
  protected readonly writeScopes = BROKER_WRITE_SCOPES;

  protected readonly editing = signal(false);
  protected readonly pending = signal<Pending>(null);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    username: ['', [Validators.required, Validators.pattern(/\S/)]],
    password: ['', Validators.required],
  });

  protected readonly tone = computed(() => TONES[this.broker().status]);

  /** The refusals belonging to this broker's own actions, each with a heading that says which. */
  protected readonly errors = computed(() => {
    const id = this.broker().brokerId;
    const all = this.store.actionErrors();
    const labelled: [string, string][] = [
      [`credentials:${id}`, 'Could not save the credentials'],
      [`remove-credentials:${id}`, 'Could not remove the credentials'],
      [`remove:${id}`, 'Could not remove the broker'],
      [`check:${id}`, 'Could not check the connection'],
    ];

    return labelled.flatMap(([action, title]) =>
      all[action] === undefined ? [] : [{ action, title, detail: all[action].detail }],
    );
  });

  constructor() {
    // A stored credential empties the form and closes it, so the password is not left in the
    // field. Only for *this* broker's action: another broker's save must not clear this form.
    effect(() => {
      if (this.store.lastSaved() === `credentials:${this.broker().brokerId}`) {
        this.form.reset();
        this.editing.set(false);
      }
    });
  }

  protected save(): void {
    if (this.form.invalid) {
      return;
    }

    const { username, password } = this.form.getRawValue();

    this.store.setCredential({
      brokerId: this.broker().brokerId,
      username: username.trim(),
      password,
    });

    // Cleared whatever the answer: if the registry refuses the pair, the next move is to type
    // the password again, and meanwhile it should not sit in the field.
    this.form.controls.password.reset();
  }

  protected confirm(): void {
    const id = this.broker().brokerId;
    const action = this.pending();

    this.pending.set(null);

    if (action === 'remove-credentials') {
      this.store.removeCredential(id);
    } else if (action === 'remove') {
      this.store.removeBroker(id);
    }
  }
}
