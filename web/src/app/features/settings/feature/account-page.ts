import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { SessionStore } from '../../../core/auth/session-store';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { checkPasswordChange, MIN_PASSWORD_LENGTH } from '../../../domain/identity/password';
import { ChangeConfirmation } from '../../../shared/ui/change-confirmation/change-confirmation';
import { AccountStore } from '../application/account-store';
import { SettingsNav } from './settings-nav';

const INPUT =
  'border-input bg-background focus-visible:ring-ring flex h-9 w-full rounded-md border px-3 py-1 text-sm focus-visible:ring-1 focus-visible:outline-none';

/**
 * The signed-in person's own account: changing their password (ADR-048).
 *
 * <b>Anyone who is signed in, with no scope.</b> Everybody owns their own password, so there is
 * nothing here to hide from a reader, and no `*cdIfScope`: the route's guard asks only for a session.
 *
 * <b>It is also the one screen a session that must change its password can reach.</b> An administrator
 * who reset the password knows the temporary one, so until the person has replaced it the registry
 * lets their session do nothing else (`password_change_required`), every other route sends them
 * here, and this page says why in the first thing on it. The settings tabs are not drawn for that
 * session: each leads to a screen that would answer 403 to everything it asked.
 *
 * <b>Every sentence in the confirmation was measured against the registry</b>, not read off its code:
 * the other sessions are signed out and the one in use stays (the .NET HTTP suite and
 * `e2e/account-lifecycle.spec.ts`), API keys are not touched, a temporary password stops working, and
 * the audit log gets `PASSWORD_CHANGED` naming the account and neither password. A registry that starts
 * doing more or less has to change this text with it.
 *
 * <b>The form blocks on what the registry would certainly refuse and on what there is no request
 * for</b>: a new password under twelve characters, the one already in force, and two boxes that
 * differ. The current password is not judged here, because only the registry can say whether it is
 * right, and a wrong one is shown as the registry answered it.
 *
 * <b>The passwords are cleared the moment they are sent</b>, whatever the answer. If the registry
 * refuses, the next move is to type them again, and meanwhile they should not sit in the fields.
 */
@Component({
  selector: 'cd-account-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [AccountStore],
  imports: [ReactiveFormsModule, HlmAlertImports, HlmButton, ChangeConfirmation, SettingsNav],
  template: `
    <section class="mx-auto w-full max-w-4xl space-y-6 p-6 lg:p-8">
      <header>
        <h1 class="text-foreground text-2xl font-bold tracking-tight">
          {{ forced ? 'Choose a new password' : 'Settings' }}
        </h1>
        <p class="text-muted-foreground mt-1">
          @if (forced) {
            <span class="font-mono break-all">{{ session.actor() }}</span>
          } @else {
            <span class="font-mono">{{ environments.name() }}</span>
          }
        </p>
      </header>

      @if (!forced) {
        <cd-settings-nav />
      }

      @if (forced) {
        <div hlmAlert role="alert">
          <h2 hlmAlertTitle>An administrator reset your password</h2>
          <p hlmAlertDescription>
            The one you signed in with is temporary, and the administrator who set it knows it.
            Choose a new one to continue: nothing else works until you have.
          </p>
        </div>
      }

      <div role="status" aria-live="polite">
        @if (store.changed(); as changed) {
          <div hlmAlert>
            <p hlmAlertDescription>
              Your password was changed.
              @if (changed.otherSessionsSignedOut === 0) {
                You had no other session to sign out.
              } @else {
                {{ changed.otherSessionsSignedOut }}
                {{
                  changed.otherSessionsSignedOut === 1 ? 'other session was' : 'other sessions were'
                }}
                signed out.
              }
            </p>
          </div>
        }
      </div>

      <form
        [formGroup]="form"
        (ngSubmit)="ask()"
        class="border-border bg-card space-y-4 rounded-xl border p-5"
        aria-labelledby="change-password-heading"
        novalidate
      >
        <div>
          <h2 id="change-password-heading" class="text-foreground text-base font-semibold">
            Change password
          </h2>
          <p class="text-muted-foreground mt-1 text-sm">
            At least {{ minLength }} characters, and no other rules. Your current password is asked
            for so that a session left open on a shared screen cannot be used to change it.
          </p>
        </div>

        @if (store.error(); as failure) {
          <div hlmAlert variant="destructive" role="alert">
            <h3 hlmAlertTitle>The registry did not change it</h3>
            <p hlmAlertDescription>{{ failure.detail }}</p>
          </div>
        }

        <div class="space-y-1.5">
          <label class="text-sm font-medium" for="account-current">Current password</label>
          <input
            id="account-current"
            type="password"
            formControlName="current"
            autocomplete="current-password"
            [class]="input"
          />
        </div>

        <div class="grid gap-3 sm:grid-cols-2">
          <div class="space-y-1.5">
            <label class="text-sm font-medium" for="account-new">New password</label>
            <input
              id="account-new"
              type="password"
              formControlName="next"
              autocomplete="new-password"
              aria-describedby="account-finding"
              [attr.aria-invalid]="finding() ? 'true' : null"
              [class]="input"
            />
          </div>
          <div class="space-y-1.5">
            <label class="text-sm font-medium" for="account-confirm">Repeat the new password</label>
            <input
              id="account-confirm"
              type="password"
              formControlName="confirmation"
              autocomplete="new-password"
              aria-describedby="account-finding"
              [class]="input"
            />
          </div>
        </div>

        <div id="account-finding" aria-live="polite">
          @if (finding(); as problem) {
            <p class="text-destructive text-xs">{{ problem.message }}</p>
          }
        </div>

        @if (confirming()) {
          <cd-change-confirmation
            [heading]="
              forced
                ? 'Replace the temporary password for ' + session.actor() + '?'
                : 'Change the password for ' + session.actor() + '?'
            "
            [confirmLabel]="forced ? 'Yes, replace it' : 'Yes, change my password'"
            busyLabel="Changing…"
            [busy]="store.submitting()"
            (confirmed)="send()"
            (declined)="confirming.set(false)"
          >
            @if (forced) {
              <li>
                <span class="text-foreground">The temporary password stops working at once.</span>
                From then on only you know your password.
              </li>
              <li>
                This session stays signed in and can do everything your role allows. Any other
                session of yours is signed out.
              </li>
            } @else {
              <li>
                <span class="text-foreground">Every other session of yours is signed out</span>, in
                other browsers and on other devices. This one stays signed in.
              </li>
              <li>Any API key you issued keeps working: a key is not made from your password.</li>
            }
            <li>
              The audit log gets one entry, <span class="font-mono">PASSWORD_CHANGED</span>, naming
              your account. Neither password is recorded anywhere.
            </li>
          </cd-change-confirmation>
        } @else {
          <button hlmBtn type="submit" [disabled]="!canAsk() || store.submitting()">
            Review change
          </button>
        }
      </form>
    </section>
  `,
})
export class AccountPage {
  protected readonly store = inject(AccountStore);
  protected readonly session = inject(SessionStore);
  protected readonly environments = inject(ActiveEnvironmentStore);
  private readonly router = inject(Router);

  protected readonly input = INPUT;
  protected readonly minLength = MIN_PASSWORD_LENGTH;

  /**
   * Whether this session must replace a password an administrator set, as it was when the page
   * opened. Read once: replacing it clears the flag, and the page that has just done its job should
   * not flip into a different one while it navigates away.
   */
  protected readonly forced = this.session.mustChangePassword();

  protected readonly form = inject(FormBuilder).nonNullable.group({
    current: [''],
    next: [''],
    confirmation: [''],
  });

  protected readonly confirming = signal(false);

  /**
   * The form's own events as a signal: a `computed` over a control's value would never recompute,
   * since an `AbstractControl` is not a signal.
   */
  private readonly events = toSignal(this.form.events);

  /** What stops the new password being sent, shown only once the person has started typing one. */
  protected readonly finding = computed(() => {
    this.events();

    const { current, next, confirmation } = this.form.getRawValue();

    return next === '' && confirmation === ''
      ? null
      : checkPasswordChange(current, next, confirmation).finding;
  });

  protected readonly canAsk = computed(() => {
    this.events();

    const { current, next, confirmation } = this.form.getRawValue();

    return (
      current !== '' &&
      next !== '' &&
      checkPasswordChange(current, next, confirmation).finding === null
    );
  });

  constructor() {
    // The point of a forced change is to get on with the app, so once the temporary password has been
    // replaced there is nothing here to keep the person for.
    effect(() => {
      if (this.forced && this.store.changed() !== null) {
        untracked(() => void this.router.navigateByUrl('/'));
      }
    });
  }

  protected ask(): void {
    if (this.canAsk() && !this.store.submitting()) {
      this.confirming.set(true);
    }
  }

  protected send(): void {
    if (!this.canAsk()) {
      return;
    }

    const { current, next } = this.form.getRawValue();

    this.confirming.set(false);
    this.store.change({ current, next });

    // Cleared whatever the answer: if the registry refuses, the next move is to type them again.
    this.form.reset();
  }
}
