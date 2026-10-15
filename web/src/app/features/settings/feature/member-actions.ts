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
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { IfScope } from '../../../core/auth/if-scope';
import type { Member } from '../../../domain/identity/member';
import { checkPasswordLength, MIN_PASSWORD_LENGTH } from '../../../domain/identity/password';
import { ORG_ADMIN_SCOPES } from '../../../domain/identity/scope';
import { ChangeConfirmation } from '../../../shared/ui/change-confirmation/change-confirmation';

const INPUT =
  'border-input bg-background focus-visible:ring-ring flex h-9 w-full rounded-md border px-3 py-1 text-sm focus-visible:ring-1 focus-visible:outline-none';

type Mode = 'reset' | 'disable' | 'enable';

/**
 * What an administrator may do to one member besides change their role: reset their password, and
 * disable or enable them (ADR-048).
 *
 * <b>Absent, not disabled, for anyone without `org:admin`</b> (ADR-018): the whole actions block is
 * inside `*cdIfScope`, so a session that cannot do these things is not shown the buttons that would
 * be refused. The members screen is already `org:admin` end to end, and this is the same rule stated
 * where it applies, so that it is not true by accident of where the component is put.
 *
 * <b>Every sentence in the three confirmations was measured against the registry</b>, not read off its
 * code: what a reset does to the member's sessions and keys and what their session can do until they
 * change it, what a disabling revokes and what an enabling does not bring back, what each writes to
 * the audit log, and that nobody is notified and no password is sent anywhere (the .NET HTTP suite and
 * `e2e/account-lifecycle.spec.ts` pin each). A registry that starts doing more or less has to change
 * this text with it.
 *
 * <b>Your own row offers neither.</b> The registry refuses both for the account the request is made
 * with (`own_account`): a reset would change a password without proving the current one is known, and
 * disabling yourself locks yourself out. The row points at the account screen instead. The last owner
 * who can sign in is not special-cased here: whether that is true is the registry's to say, and it says
 * so beside the control that asked.
 *
 * <b>It sends nothing itself.</b> It reports what was confirmed and closes, and the page decides what
 * to do with it; the registry's answer comes back in through `busy` and `error`. A temporary password
 * is typed into a field, handed to the page on the second click and cleared at once, and is never
 * state.
 */
@Component({
  selector: 'cd-member-actions',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    RouterLink,
    HlmAlertImports,
    HlmButton,
    IfScope,
    ChangeConfirmation,
  ],
  host: { class: 'block' },
  template: `
    <ng-container *cdIfScope="orgAdmin">
      @if (self()) {
        <p class="text-muted-foreground text-xs">
          This is you.
          <a routerLink="/settings/account" class="text-foreground underline underline-offset-2"
            >Change your own password</a
          >
          under Account.
        </p>
      } @else {
        <div class="space-y-3">
          @if (mode() === null) {
            <div class="flex flex-wrap gap-2">
              <button
                hlmBtn
                type="button"
                variant="outline"
                size="sm"
                [disabled]="busy()"
                (click)="open('reset')"
              >
                Reset password…
              </button>
              @if (member().disabled) {
                <button
                  hlmBtn
                  type="button"
                  variant="outline"
                  size="sm"
                  [disabled]="busy()"
                  (click)="open('enable')"
                >
                  Enable…
                </button>
              } @else {
                <button
                  hlmBtn
                  type="button"
                  variant="outline"
                  size="sm"
                  [disabled]="busy()"
                  (click)="open('disable')"
                >
                  Disable…
                </button>
              }
            </div>
          }

          @if (mode() === 'reset') {
            <form (submit)="$event.preventDefault(); review()" class="space-y-3" novalidate>
              <div class="space-y-1.5">
                <label class="text-sm font-medium" [for]="'reset-' + member().userId">
                  Temporary password for {{ member().email }}
                </label>
                <input
                  [id]="'reset-' + member().userId"
                  type="password"
                  [formControl]="password"
                  autocomplete="new-password"
                  [attr.aria-describedby]="'reset-help-' + member().userId"
                  [class]="input"
                />
                <p
                  [id]="'reset-help-' + member().userId"
                  class="text-muted-foreground text-xs"
                  aria-live="polite"
                >
                  @if (finding(); as problem) {
                    <span class="text-destructive">{{ problem.message }}</span>
                  } @else {
                    At least {{ minLength }} characters. They must replace it the next time they
                    sign in.
                  }
                </p>
              </div>

              @if (!reviewing()) {
                <div class="flex flex-wrap gap-2">
                  <button hlmBtn type="submit" size="sm" [disabled]="!sendable()">
                    Review change
                  </button>
                  <button hlmBtn type="button" variant="ghost" size="sm" (click)="close()">
                    Cancel
                  </button>
                </div>
              } @else {
                <cd-change-confirmation
                  [heading]="'Reset the password for ' + member().email + '?'"
                  confirmLabel="Yes, reset it"
                  busyLabel="Resetting…"
                  [busy]="busy()"
                  (confirmed)="sendReset()"
                  (declined)="reviewing.set(false)"
                >
                  <li>
                    <span class="text-foreground"
                      >Their password becomes the one you typed, and they must replace it.</span
                    >
                    Until they have, their session can do nothing but change it.
                  </li>
                  <li>
                    Every session they have is signed out now. Their API keys are not touched.
                  </li>
                  <li>
                    You will know their temporary password. Pass it on by a way you trust: the
                    registry does not send it, and does not show it again.
                  </li>
                  <li>
                    The audit log gets one entry,
                    <span class="font-mono">MEMBER_PASSWORD_RESET</span>, naming you. The password
                    is not recorded.
                  </li>
                </cd-change-confirmation>
              }
            </form>
          }

          @if (mode() === 'disable') {
            <cd-change-confirmation
              [heading]="'Disable ' + member().email + '?'"
              confirmLabel="Yes, disable them"
              busyLabel="Disabling…"
              declineLabel="Keep them enabled"
              [busy]="busy()"
              (confirmed)="sendDisabled(true)"
              (declined)="close()"
            >
              <li>
                <span class="text-foreground">They can no longer sign in,</span> and every session
                they have stops working at once.
              </li>
              <li>
                Every API key they issued is revoked for good. Enabling them again does not bring a
                key back; it is issued again on purpose.
              </li>
              <li>
                They stay in this list, marked DISABLED, with their role and everything the audit
                log says they did.
              </li>
              <li>
                The audit log gets one entry, <span class="font-mono">MEMBER_DISABLED</span>, naming
                you. Nobody is notified.
              </li>
              @if (member().role === 'OWNER') {
                <li>
                  The registry refuses if they are the last owner who can sign in: there would be
                  nobody left to manage members.
                </li>
              }
            </cd-change-confirmation>
          }

          @if (mode() === 'enable') {
            <cd-change-confirmation
              [heading]="'Enable ' + member().email + '?'"
              confirmLabel="Yes, enable them"
              busyLabel="Enabling…"
              declineLabel="Keep them disabled"
              [busy]="busy()"
              (confirmed)="sendDisabled(false)"
              (declined)="close()"
            >
              <li>
                <span class="text-foreground"
                  >They can sign in again with the password they had.</span
                >
              </li>
              <li>
                Nothing that was revoked when they were disabled comes back: they get new sessions
                when they sign in, and any API key is issued again.
              </li>
              <li>
                The audit log gets one entry, <span class="font-mono">MEMBER_ENABLED</span>, naming
                you. Nobody is notified.
              </li>
            </cd-change-confirmation>
          }
        </div>
      }

      @if (error(); as failure) {
        <div hlmAlert variant="destructive" class="mt-3" role="alert">
          <h3 hlmAlertTitle>The registry refused this change</h3>
          <p hlmAlertDescription>{{ failure }}</p>
        </div>
      }
    </ng-container>
  `,
})
export class MemberActions {
  /** The member, as the registry last said. */
  readonly member = input.required<Member>();

  /** Whether this is the signed-in person's own row. */
  readonly self = input(false);

  /** Whether the registry has not answered the last confirmed change yet. */
  readonly busy = input(false);

  /** The registry's refusal of the last attempt, in its own words (its `detail`), or null. */
  readonly error = input<string | null>(null);

  /** The reset was confirmed, with the temporary password. It is the last place the password exists. */
  readonly resetConfirmed = output<string>();

  /** A disabling (true) or an enabling (false) was confirmed. */
  readonly disabledConfirmed = output<boolean>();

  protected readonly input = INPUT;
  protected readonly orgAdmin = ORG_ADMIN_SCOPES;
  protected readonly minLength = MIN_PASSWORD_LENGTH;

  protected readonly mode = signal<Mode | null>(null);
  protected readonly reviewing = signal(false);

  protected readonly password = new FormControl('', { nonNullable: true });

  private readonly events = toSignal(this.password.events);

  /** Shown once there is something typed to be wrong about. */
  protected readonly finding = computed(() => {
    this.events();

    return this.password.value === '' ? null : checkPasswordLength(this.password.value);
  });

  protected readonly sendable = computed(() => {
    this.events();

    return this.password.value !== '' && checkPasswordLength(this.password.value) === null;
  });

  /** Who this row is and what state they are in, so a change of either is one thing to watch. */
  private readonly state = computed(() => `${this.member().userId}:${this.member().disabled}`);

  constructor() {
    // A confirmation belongs to the state it was opened in. Once the registry has disabled the member
    // the row is offering the opposite step, and a disabling confirmation left open would turn into an
    // enabling one on the same row, one click from the thing it has not asked about.
    effect(() => {
      this.state();

      untracked(() => this.close());
    });
  }

  protected open(mode: Mode): void {
    this.mode.set(mode);
  }

  protected close(): void {
    this.mode.set(null);
    this.reviewing.set(false);
    this.password.reset();
  }

  protected review(): void {
    if (this.sendable()) {
      this.reviewing.set(true);
    }
  }

  protected sendReset(): void {
    if (!this.sendable()) {
      return;
    }

    const password = this.password.value;

    // Cleared before it is handed on, whatever the answer: if the registry refuses, the next move is
    // to type it again, and meanwhile it should not sit in the field.
    this.close();
    this.resetConfirmed.emit(password);
  }

  protected sendDisabled(disabled: boolean): void {
    this.close();
    this.disabledConfirmed.emit(disabled);
  }
}
