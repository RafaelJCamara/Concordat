import { ChangeDetectionStrategy, Component, DOCUMENT, effect, inject } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { SessionStore } from '../../../core/auth/session-store';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { ROLE_MEANING, ROLES, type Member, type Role } from '../../../domain/identity/member';
import { RelativeTimePipe } from '../../../shared/pipes/relative-time-pipe';
import { StatusBadge } from '../../../shared/ui/status-badge/status-badge';
import { MembersStore } from '../application/members-store';
import { MemberActions } from './member-actions';
import { SettingsNav } from './settings-nav';

const INPUT =
  'border-input bg-background focus-visible:ring-ring flex h-9 w-full rounded-md border px-3 py-1 text-sm focus-visible:ring-1 focus-visible:outline-none';

/** The shortest password the registry accepts. Checked here only to say so before sending. */
const MIN_PASSWORD = 12;

/**
 * The people in the organisation.
 *
 * <b>This whole screen is `org:admin`</b> — the members endpoints require it to read as well as
 * to write — so the route is guarded and the tab is absent for anyone else; there is no
 * read-only version to fall back to. Everything on it is therefore a write affordance for
 * somebody entitled to it, and nothing needs `*cdIfScope`.
 *
 * <b>A password is asked for, never shown.</b> Adding a member sets their initial password, and
 * the field is `type=password` with `autocomplete="new-password"` so a password manager does not
 * offer to fill it with the signed-in admin's own. The length rule is stated beside the field
 * and the registry's own refusal is what is rendered when it is broken — the same twelve
 * characters, said in one place that cannot drift from the other.
 *
 * <b>A role change is applied from the registry's answer.</b> Whether it is allowed is the
 * registry's to say: the last owner who can sign in cannot be demoted, because an instance whose
 * last owner is a reader cannot be administered by anybody.
 *
 * <b>Each row also carries what an administrator may do to the account</b> (ADR-048): reset the
 * password and disable or enable it, in `cd-member-actions`, each behind a confirmation whose every
 * sentence was measured against the registry. What those did is applied from the registry's answer,
 * with the registry's own count of what it ended, and a refusal stays with the row it concerns.
 */
@Component({
  selector: 'cd-members-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [MembersStore],
  imports: [
    ReactiveFormsModule,
    HlmAlertImports,
    HlmButton,
    HlmSkeleton,
    MemberActions,
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

      <div role="status" aria-live="polite" class="space-y-2">
        @if (store.added(); as added) {
          <div hlmAlert>
            <p hlmAlertDescription>
              Added <span class="font-mono">{{ added.email }}</span> as {{ added.role }}. They can
              sign in now.
            </p>
          </div>
        }
        @if (store.notice(); as notice) {
          <div hlmAlert>
            <p hlmAlertDescription>
              @switch (notice.kind) {
                @case ('reset') {
                  Reset the password for <span class="font-mono">{{ notice.email }}</span
                  >. {{ notice.count }} {{ notice.count === 1 ? 'session was' : 'sessions were' }}
                  signed out, and they must choose a new password when they next sign in.
                }
                @case ('disabled') {
                  Disabled <span class="font-mono">{{ notice.email }}</span
                  >. {{ notice.count }}
                  {{ notice.count === 1 ? 'credential was' : 'credentials were' }} revoked.
                }
                @case ('enabled') {
                  Enabled <span class="font-mono">{{ notice.email }}</span
                  >. They can sign in again.
                }
              }
            </p>
          </div>
        }
      </div>

      @if (store.error(); as error) {
        <div hlmAlert variant="destructive">
          <h2 hlmAlertTitle>Could not load members</h2>
          <p hlmAlertDescription>{{ error.detail }}</p>
          <button hlmBtn hlmAlertAction variant="outline" size="sm" (click)="store.load()">
            Try again
          </button>
        </div>
      } @else if (store.loading()) {
        <div class="space-y-2" aria-busy="true" aria-live="polite">
          <div hlmSkeleton class="h-14 w-full rounded-xl"></div>
          <div hlmSkeleton class="h-14 w-full rounded-xl"></div>
        </div>
      } @else {
        <ul class="space-y-2">
          @for (member of store.members(); track member.userId) {
            <li class="border-border bg-card rounded-xl border p-4">
              <div class="flex flex-wrap items-center gap-3">
                <div class="min-w-0">
                  <p class="text-foreground font-mono text-sm font-medium break-all">
                    {{ member.email }}
                  </p>
                  <p class="text-muted-foreground text-xs">
                    {{ member.displayName }} ·
                    @if (member.lastSignedInAt; as last) {
                      signed in
                      <span [title]="last.toISOString()">{{ last | cdRelativeTime }}</span>
                    } @else {
                      never signed in
                    }
                  </p>
                </div>

                @if (member.disabled) {
                  <cd-status-badge tone="destructive">DISABLED</cd-status-badge>
                }
                @if (member.mustChangePassword) {
                  <cd-status-badge tone="warning">MUST CHANGE PASSWORD</cd-status-badge>
                }

                <div class="ms-auto flex items-center gap-2">
                  <label class="sr-only" [for]="'role-' + member.userId">
                    Role for {{ member.email }}
                  </label>
                  <select
                    [id]="'role-' + member.userId"
                    [class]="input + ' w-32'"
                    [disabled]="store.changing().includes(member.userId)"
                    (change)="pick(member, $event)"
                  >
                    @for (role of roles; track role) {
                      <option [value]="role" [selected]="role === member.role">{{ role }}</option>
                    }
                  </select>
                </div>
              </div>

              @if (store.roleErrors()[member.userId]; as error) {
                <div hlmAlert variant="destructive" class="mt-3">
                  <h3 hlmAlertTitle>Could not change the role</h3>
                  <p hlmAlertDescription>{{ error.detail }}</p>
                </div>
              }

              <cd-member-actions
                class="border-border mt-3 border-t pt-3"
                [member]="member"
                [self]="isSelf(member)"
                [busy]="store.acting().includes(member.userId)"
                [error]="store.accountErrors()[member.userId]?.detail ?? null"
                (resetConfirmed)="store.resetPassword({ userId: member.userId, password: $event })"
                (disabledConfirmed)="store.setDisabled({ userId: member.userId, disabled: $event })"
              />
            </li>
          }
        </ul>

        <form
          [formGroup]="form"
          (ngSubmit)="add()"
          class="border-border bg-card space-y-3 rounded-xl border p-5"
          aria-labelledby="add-member-heading"
        >
          <h2 id="add-member-heading" class="text-foreground text-sm font-semibold">
            Add a member
          </h2>

          <div class="grid gap-3 sm:grid-cols-2">
            <div class="space-y-1.5">
              <label class="text-sm font-medium" for="member-email">Email</label>
              <input
                id="member-email"
                type="email"
                formControlName="email"
                autocomplete="off"
                [class]="input"
              />
            </div>
            <div class="space-y-1.5">
              <label class="text-sm font-medium" for="member-name">
                Display name <span class="text-muted-foreground font-normal">· optional</span>
              </label>
              <input
                id="member-name"
                formControlName="displayName"
                autocomplete="off"
                [class]="input"
              />
            </div>
          </div>

          <div class="grid gap-3 sm:grid-cols-2">
            <div class="space-y-1.5">
              <label class="text-sm font-medium" for="member-password">Initial password</label>
              <input
                id="member-password"
                type="password"
                formControlName="password"
                autocomplete="new-password"
                [class]="input"
              />
              <p class="text-muted-foreground text-xs">At least {{ minPassword }} characters.</p>
            </div>
            <div class="space-y-1.5">
              <label class="text-sm font-medium" for="member-role">Role</label>
              <select id="member-role" formControlName="role" [class]="input">
                @for (role of roles; track role) {
                  <option [value]="role">{{ role }}</option>
                }
              </select>
              <p class="text-muted-foreground text-xs">{{ meaning(form.controls.role.value) }}</p>
            </div>
          </div>

          @if (store.addError(); as error) {
            <div hlmAlert variant="destructive">
              <h3 hlmAlertTitle>Could not add the member</h3>
              <p hlmAlertDescription>{{ error.detail }}</p>
            </div>
          }

          <button type="submit" hlmBtn size="sm" [disabled]="form.invalid || store.adding()">
            Add member
          </button>
        </form>
      }
    </section>
  `,
})
export class MembersPage {
  protected readonly store = inject(MembersStore);
  protected readonly environments = inject(ActiveEnvironmentStore);
  private readonly session = inject(SessionStore);

  protected readonly input = INPUT;
  protected readonly roles = ROLES;
  protected readonly minPassword = MIN_PASSWORD;

  protected readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    displayName: [''],
    password: ['', [Validators.required, Validators.minLength(MIN_PASSWORD)]],
    role: ['READER' as Role],
  });

  private readonly document = inject(DOCUMENT);

  constructor() {
    effect(() => {
      this.store.load();
    });

    // A refused role change puts the selector back on the role the member still has. The
    // browser has already moved it to what was picked, and a selector left saying ADMIN beside
    // a member who is still a READER is a screen that lies about the one thing it is for.
    effect(() => {
      for (const userId of Object.keys(this.store.roleErrors())) {
        const member = this.store.members().find((m) => m.userId === userId);
        const select = this.document.getElementById(`role-${userId}`) as HTMLSelectElement | null;

        if (member !== undefined && select !== null) {
          select.value = member.role;
        }
      }
    });

    // A created member empties the form, the password above all: it should not sit in the field
    // for the next person at the screen, and a second click must not send it again.
    effect(() => {
      if (this.store.added() !== null) {
        this.form.reset();
      }
    });
  }

  protected meaning(role: Role): string {
    return ROLE_MEANING[role];
  }

  /**
   * Whether a row is the signed-in person's own. A session's actor is the account's email, which is
   * how the registry names a person in the audit trail, so the two are compared as the registry
   * stores them: lowercased.
   */
  protected isSelf(member: Member): boolean {
    return member.email === this.session.actor()?.toLowerCase();
  }

  protected pick(member: Member, event: Event): void {
    const role = (event.target as HTMLSelectElement).value as Role;

    if (role !== member.role) {
      this.store.changeRole({ userId: member.userId, role });
    }
  }

  protected add(): void {
    if (this.form.invalid) {
      return;
    }

    const value = this.form.getRawValue();

    this.store.add({
      email: value.email.trim(),
      password: value.password,
      role: value.role,
      displayName: value.displayName,
    });

    // Cleared whatever the answer: if the registry refuses, the next move is to type the
    // password again, and meanwhile it should not sit in the field.
    this.form.controls.password.reset();
  }
}
