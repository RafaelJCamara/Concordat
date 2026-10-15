import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';
import { SessionStore } from '../../../core/auth/session-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { ApiKey, IssuedApiKey, Member, Role } from '../../../domain/identity/member';
import type { Scope } from '../../../domain/identity/scope';
import { ApiKeysStore } from '../application/api-keys-store';
import { MembersStore, type AccountNotice } from '../application/members-store';
import { ApiKeysPage } from './api-keys-page';
import { MembersPage } from './members-page';

// The screens' own decisions. Members: that a password is asked for and never shown, that the
// form empties itself, and that a refused role change puts the selector back. API keys: that the
// secret is shown once and stays until dismissed, that only the issuer's own scopes are offered
// and none is pre-ticked, and that revoking takes two clicks.

function member(overrides: Partial<Member> = {}): Member {
  return {
    userId: 'u1',
    email: 'alice@example.com',
    displayName: 'Alice',
    role: 'READER',
    disabled: false,
    mustChangePassword: false,
    createdAt: new Date('2026-08-01T09:00:00Z'),
    lastSignedInAt: null,
    ...overrides,
  };
}

function apiKey(overrides: Partial<ApiKey> = {}): ApiKey {
  return {
    id: 'k1',
    keyId: 'abc123',
    label: 'ci-pipeline',
    scopes: ['subject:write', 'ci'],
    createdAt: new Date('2026-08-01T09:00:00Z'),
    expiresAt: null,
    lastUsedAt: null,
    revokedAt: null,
    ...overrides,
  };
}

function fill(host: HTMLElement, id: string, value: string): void {
  const element = host.querySelector(`#${id}`) as HTMLInputElement | HTMLSelectElement;
  element.value = value;
  element.dispatchEvent(new Event(element instanceof HTMLSelectElement ? 'change' : 'input'));
}

function button(host: HTMLElement, text: string): HTMLButtonElement | undefined {
  return [...host.querySelectorAll('button')].find((b) => b.textContent?.trim() === text);
}

function renderMembers(
  state: {
    members?: readonly Member[];
    loading?: boolean;
    error?: ConcordatError | null;
    adding?: boolean;
    addError?: ConcordatError | null;
    added?: Member | null;
    changing?: readonly string[];
    roleErrors?: Record<string, ConcordatError>;
    acting?: readonly string[];
    accountErrors?: Record<string, ConcordatError>;
    notice?: AccountNotice | null;
    scopes?: readonly Scope[];
    actor?: string;
  } = {},
) {
  const add = vi.fn();
  const changeRole = vi.fn();
  const resetPassword = vi.fn();
  const setDisabled = vi.fn();
  const added = signal(state.added ?? null);
  const roleErrors = signal(state.roleErrors ?? {});
  const members = signal(state.members ?? [member()]);
  const notice = signal(state.notice ?? null);
  const accountErrors = signal(state.accountErrors ?? {});

  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });
  TestBed.overrideComponent(MembersPage, {
    set: {
      providers: [
        {
          provide: MembersStore,
          useValue: {
            members,
            loading: signal(state.loading ?? false),
            error: signal(state.error ?? null),
            adding: signal(state.adding ?? false),
            addError: signal(state.addError ?? null),
            added,
            changing: signal(state.changing ?? []),
            roleErrors,
            acting: signal(state.acting ?? []),
            accountErrors,
            notice,
            load: () => {},
            add,
            changeRole,
            resetPassword,
            setDisabled,
          },
        },
      ],
    },
  });
  TestBed.inject(SessionStore).signIn({
    credential: 'c',
    actor: state.actor ?? 'a',
    scopes: state.scopes ?? ['org:admin'],
  });

  const fixture = TestBed.createComponent(MembersPage);
  fixture.detectChanges();

  return {
    fixture,
    host: fixture.nativeElement as HTMLElement,
    add,
    changeRole,
    resetPassword,
    setDisabled,
    added,
    members,
    roleErrors,
    accountErrors,
    notice,
  };
}

describe('MembersPage', () => {
  it('lists each member with their email, name and when they last signed in', () => {
    const { host } = renderMembers({
      members: [member({ lastSignedInAt: new Date('2026-08-13T09:00:00Z') })],
    });

    expect(host.textContent).toContain('alice@example.com');
    expect(host.textContent).toContain('Alice');
    expect(host.textContent).toContain('signed in');
  });

  it('says a member who has never signed in has not', () => {
    expect(renderMembers().host.textContent).toContain('never signed in');
  });

  it('marks a disabled member', () => {
    expect(renderMembers({ members: [member({ disabled: true })] }).host.textContent).toContain(
      'DISABLED',
    );
  });

  it('renders a failed load with a retry', () => {
    const { host } = renderMembers({
      error: new ConcordatError({
        status: 403,
        code: 'insufficient_scope',
        detail: 'Needs org:admin.',
      }),
    });

    expect(host.textContent).toContain('Could not load members');
    expect(host.textContent).toContain('Needs org:admin.');
  });

  describe('roles', () => {
    it('offers all three, on the member’s current one', () => {
      const { host } = renderMembers({ members: [member({ role: 'ADMIN' })] });
      const select = host.querySelector('#role-u1') as HTMLSelectElement;

      expect([...select.options].map((o) => o.value)).toEqual(['READER', 'ADMIN', 'OWNER']);
      expect(select.value).toBe('ADMIN');
    });

    it('labels each selector with whose it is, for a screen reader', () => {
      expect(renderMembers().host.querySelector('label[for="role-u1"]')?.textContent).toContain(
        'alice@example.com',
      );
    });

    it('sends a change only when the role actually differs', () => {
      const { host, changeRole } = renderMembers();

      fill(host, 'role-u1', 'READER');
      expect(changeRole).not.toHaveBeenCalled();

      fill(host, 'role-u1', 'ADMIN');
      expect(changeRole).toHaveBeenCalledWith({ userId: 'u1', role: 'ADMIN' });
    });

    it('disables a selector while its change is in flight', () => {
      const { host } = renderMembers({ changing: ['u1'] });

      expect((host.querySelector('#role-u1') as HTMLSelectElement).disabled).toBe(true);
    });

    it('shows a refusal on the member, and puts the selector back where the registry has them', () => {
      const { fixture, host, roleErrors } = renderMembers({ members: [member({ role: 'OWNER' })] });

      fill(host, 'role-u1', 'READER');
      expect((host.querySelector('#role-u1') as HTMLSelectElement).value).toBe('READER');

      roleErrors.set({
        u1: new ConcordatError({
          status: 400,
          code: 'cannot_demote_self',
          detail: 'You are the last owner.',
        }),
      });
      fixture.detectChanges();

      // The browser had already moved it; a selector saying READER beside an OWNER is a screen
      // that lies about the one thing it is for.
      expect((host.querySelector('#role-u1') as HTMLSelectElement).value).toBe('OWNER');
      expect(host.textContent).toContain('You are the last owner.');
    });
  });

  describe('adding', () => {
    function fillAll(host: HTMLElement, overrides: Record<string, string> = {}): void {
      const values: Record<string, string> = {
        'member-email': 'new@example.com',
        'member-password': 'correct horse battery',
        'member-role': 'READER',
        ...overrides,
      };

      for (const [id, value] of Object.entries(values)) {
        fill(host, id, value);
      }
    }

    it('sends the trimmed email, the role and the password', () => {
      const { fixture, host, add } = renderMembers();

      fillAll(host, { 'member-email': ' new@example.com ', 'member-role': 'ADMIN' });
      fixture.detectChanges();
      host.querySelector('form')!.dispatchEvent(new Event('submit'));

      expect(add).toHaveBeenCalledWith({
        email: 'new@example.com',
        password: 'correct horse battery',
        role: 'ADMIN' as Role,
        displayName: '',
      });
    });

    it('sends nothing for a password under twelve characters, and says the rule beside the field', () => {
      const { fixture, host, add } = renderMembers();

      fillAll(host, { 'member-password': 'too short' });
      fixture.detectChanges();
      host.querySelector('form')!.dispatchEvent(new Event('submit'));

      expect(add).not.toHaveBeenCalled();
      expect(host.textContent).toContain('At least 12 characters');
    });

    it('sends nothing for an address that is not one', () => {
      const { fixture, host, add } = renderMembers();

      fillAll(host, { 'member-email': 'not-an-email' });
      fixture.detectChanges();
      host.querySelector('form')!.dispatchEvent(new Event('submit'));

      expect(add).not.toHaveBeenCalled();
    });

    it('keeps the password out of a password manager’s reach, and out of sight', () => {
      const password = renderMembers().host.querySelector('#member-password') as HTMLInputElement;

      expect(password.type).toBe('password');
      expect(password.getAttribute('autocomplete')).toBe('new-password');
    });

    it('empties the password whatever the answer, so it does not sit in the field', () => {
      const { fixture, host } = renderMembers();

      fillAll(host);
      fixture.detectChanges();
      host.querySelector('form')!.dispatchEvent(new Event('submit'));

      expect((host.querySelector('#member-password') as HTMLInputElement).value).toBe('');
      // But the address stays: if the registry refuses, the next move is to edit it.
      expect((host.querySelector('#member-email') as HTMLInputElement).value).toBe(
        'new@example.com',
      );
    });

    it('empties the whole form once the member exists', () => {
      const { fixture, host, added } = renderMembers();

      fillAll(host);
      fixture.detectChanges();
      added.set(member({ email: 'new@example.com', role: 'READER' }));
      fixture.detectChanges();

      expect((host.querySelector('#member-email') as HTMLInputElement).value).toBe('');
    });

    it('says who was added and as what', () => {
      const { host } = renderMembers({
        added: member({ email: 'new@example.com', role: 'ADMIN' }),
      });

      expect(host.querySelector('[role="status"]')?.textContent).toContain('new@example.com');
      expect(host.querySelector('[role="status"]')?.textContent).toContain('ADMIN');
    });

    it('shows the registry’s refusal under the form', () => {
      const { host } = renderMembers({
        addError: new ConcordatError({
          status: 409,
          code: 'user_already_exists',
          detail: 'Already a member.',
        }),
      });

      expect(host.textContent).toContain('Could not add the member');
      expect(host.textContent).toContain('Already a member.');
    });

    it('explains the chosen role', () => {
      const { fixture, host } = renderMembers();

      fill(host, 'member-role', 'OWNER');
      fixture.detectChanges();

      expect(host.textContent).toContain('plus members and API keys');
    });
  });

  describe('account actions (ADR-048)', () => {
    const bob = () => member({ userId: 'u2', email: 'bob@example.com', displayName: 'Bob' });

    function click(host: HTMLElement, text: string, fixture: { detectChanges(): void }): void {
      const target = button(host, text);
      expect(target, `a button called "${text}"`).toBeDefined();
      target!.click();
      fixture.detectChanges();
    }

    it('marks a member who has not replaced a temporary password, which is who an administrator is waiting on', () => {
      const { host } = renderMembers({ members: [member({ mustChangePassword: true })] });

      expect(host.textContent).toContain('MUST CHANGE PASSWORD');
    });

    it('marks nobody who has not been reset', () => {
      expect(renderMembers().host.textContent).not.toContain('MUST CHANGE PASSWORD');
    });

    it('offers a reset and a disabling on somebody else’s row, and an enabling on a disabled one', () => {
      const { host } = renderMembers({
        members: [bob(), member({ disabled: true })],
      });

      const rows = [...host.querySelectorAll('li')].filter((li) => li.textContent?.includes('@'));
      const bobsRow = rows.find((li) => li.textContent?.includes('bob@example.com'))!;
      const aliceRow = rows.find((li) => li.textContent?.includes('alice@example.com'))!;

      expect(button(bobsRow as HTMLElement, 'Reset password…')).toBeDefined();
      expect(button(bobsRow as HTMLElement, 'Disable…')).toBeDefined();
      expect(button(bobsRow as HTMLElement, 'Enable…')).toBeUndefined();

      expect(button(aliceRow as HTMLElement, 'Reset password…')).toBeDefined();
      expect(button(aliceRow as HTMLElement, 'Enable…')).toBeDefined();
      expect(button(aliceRow as HTMLElement, 'Disable…')).toBeUndefined();
    });

    it('offers neither on your own row, and points at the account screen instead', () => {
      // The registry refuses both for the account the request is made with (`own_account`).
      const { host } = renderMembers({
        members: [member({ email: 'ops@example.com' })],
        actor: 'ops@example.com',
      });

      expect(button(host, 'Reset password…')).toBeUndefined();
      expect(button(host, 'Disable…')).toBeUndefined();
      expect(host.textContent).toContain('This is you');
      expect(host.querySelector('a[href="/settings/account"]')).not.toBeNull();
    });

    it('compares the signed-in actor to the email the way the registry stores it, lowercased', () => {
      const { host } = renderMembers({
        members: [member({ email: 'ops@example.com' })],
        actor: 'Ops@Example.com',
      });

      expect(button(host, 'Disable…')).toBeUndefined();
    });

    it('draws none of it for a session that does not hold org:admin, rather than drawing it disabled', () => {
      // ADR-018: absent, not disabled. The screen is org:admin end to end, and the controls say so
      // themselves rather than being right by accident of where they are placed.
      const { host } = renderMembers({ members: [bob()], scopes: ['subject:read'] });

      expect(button(host, 'Reset password…')).toBeUndefined();
      expect(button(host, 'Disable…')).toBeUndefined();
      expect(host.querySelector('cd-member-actions')?.textContent?.trim() ?? '').toBe('');
    });

    it('asks twice before disabling: the first click only says what it will do', () => {
      const { fixture, host, setDisabled } = renderMembers({ members: [bob()] });

      click(host, 'Disable…', fixture);

      expect(setDisabled).not.toHaveBeenCalled();
      expect(host.textContent).toContain('Disable bob@example.com?');
      expect(host.textContent).toContain('MEMBER_DISABLED');

      click(host, 'Yes, disable them', fixture);

      expect(setDisabled).toHaveBeenCalledWith({ userId: 'u2', disabled: true });
    });

    it('goes back from a disabling without sending anything', () => {
      const { fixture, host, setDisabled } = renderMembers({ members: [bob()] });

      click(host, 'Disable…', fixture);
      click(host, 'Keep them enabled', fixture);

      expect(setDisabled).not.toHaveBeenCalled();
      expect(button(host, 'Disable…')).toBeDefined();
    });

    it('asks twice before enabling, and says nothing comes back', () => {
      const { fixture, host, setDisabled } = renderMembers({
        members: [member({ userId: 'u2', email: 'bob@example.com', disabled: true })],
      });

      click(host, 'Enable…', fixture);

      expect(setDisabled).not.toHaveBeenCalled();
      expect(host.textContent).toContain('Enable bob@example.com?');
      expect(host.textContent).toContain('Nothing that was revoked');

      click(host, 'Yes, enable them', fixture);

      expect(setDisabled).toHaveBeenCalledWith({ userId: 'u2', disabled: false });
    });

    it('tells an administrator about the last-owner refusal only on an owner', () => {
      const owner = renderMembers({ members: [member({ userId: 'u2', role: 'OWNER' })] });
      click(owner.host, 'Disable…', owner.fixture);
      expect(owner.host.textContent).toContain('last owner who can sign in');

      TestBed.resetTestingModule();

      const reader = renderMembers({ members: [bob()] });
      click(reader.host, 'Disable…', reader.fixture);
      expect(reader.host.textContent).not.toContain('last owner who can sign in');
    });

    it('closes a disabling confirmation once the registry has disabled the member', () => {
      // Otherwise it would turn into an enabling confirmation on the same row, one click from a
      // thing it has not asked about.
      const { fixture, host, members, setDisabled } = renderMembers({ members: [bob()] });

      click(host, 'Disable…', fixture);
      members.set([{ ...bob(), disabled: true }]);
      fixture.detectChanges();

      expect(host.textContent).not.toContain('Yes, enable them');
      expect(host.textContent).not.toContain('Yes, disable them');
      expect(setDisabled).not.toHaveBeenCalled();
    });

    it('disables the row’s buttons while the registry has not answered', () => {
      const { host } = renderMembers({ members: [bob()], acting: ['u2'] });

      expect((button(host, 'Disable…') as HTMLButtonElement).disabled).toBe(true);
      expect((button(host, 'Reset password…') as HTMLButtonElement).disabled).toBe(true);
    });

    it('shows the registry’s refusal on the row it concerns', () => {
      const { host } = renderMembers({
        members: [bob(), member()],
        accountErrors: {
          u2: new ConcordatError({
            status: 409,
            code: 'last_owner',
            detail: 'That would leave this instance with no owner who can sign in.',
          }),
        },
      });

      const rows = [...host.querySelectorAll('li')].filter((li) => li.textContent?.includes('@'));
      const bobsRow = rows.find((li) => li.textContent?.includes('bob@example.com'))!;
      const aliceRow = rows.find((li) => li.textContent?.includes('alice@example.com'))!;

      expect(bobsRow.textContent).toContain('The registry refused this change');
      expect(bobsRow.textContent).toContain('no owner who can sign in');
      expect(aliceRow.textContent).not.toContain('The registry refused this change');
    });

    describe('resetting a password', () => {
      function openReset(state: Parameters<typeof renderMembers>[0] = { members: [bob()] }) {
        const context = renderMembers(state);
        click(context.host, 'Reset password…', context.fixture);
        return context;
      }

      const typeInto = (host: HTMLElement, fixture: { detectChanges(): void }, value: string) => {
        fill(host, 'reset-u2', value);
        fixture.detectChanges();
      };

      it('asks for a temporary password in a field a password manager will not fill', () => {
        const { host } = openReset();
        const field = host.querySelector('#reset-u2') as HTMLInputElement;

        expect(field.type).toBe('password');
        expect(field.getAttribute('autocomplete')).toBe('new-password');
        expect(host.querySelector('label[for="reset-u2"]')?.textContent).toContain(
          'bob@example.com',
        );
      });

      it('sends nothing for a password under twelve characters, and says why beside the field', () => {
        const { fixture, host, resetPassword } = openReset();

        typeInto(host, fixture, 'too short');

        expect((button(host, 'Review change') as HTMLButtonElement).disabled).toBe(true);
        expect(host.textContent).toContain('at least 12 characters');
        expect(resetPassword).not.toHaveBeenCalled();
      });

      it('asks twice: the review says what it will do, and only the second click sends', () => {
        const { fixture, host, resetPassword } = openReset();

        typeInto(host, fixture, 'a temporary one!');
        click(host, 'Review change', fixture);

        expect(resetPassword).not.toHaveBeenCalled();
        expect(host.textContent).toContain('Reset the password for bob@example.com?');
        expect(host.textContent).toContain('MEMBER_PASSWORD_RESET');
        expect(host.textContent).toContain('their session can do nothing but change it');
        expect(host.textContent).toContain('API keys are not touched');
        expect(host.textContent).toContain('does not send it');

        click(host, 'Yes, reset it', fixture);

        expect(resetPassword).toHaveBeenCalledWith({
          userId: 'u2',
          password: 'a temporary one!',
        });
      });

      it('clears the password the moment it is sent, so it does not sit in the field', () => {
        const { fixture, host } = openReset();

        typeInto(host, fixture, 'a temporary one!');
        click(host, 'Review change', fixture);
        click(host, 'Yes, reset it', fixture);

        // The form is closed and its field gone; opening it again finds it empty.
        expect(host.querySelector('#reset-u2')).toBeNull();
        click(host, 'Reset password…', fixture);
        expect((host.querySelector('#reset-u2') as HTMLInputElement).value).toBe('');
      });

      it('goes back to the field from the review with what was typed', () => {
        const { fixture, host, resetPassword } = openReset();

        typeInto(host, fixture, 'a temporary one!');
        click(host, 'Review change', fixture);
        click(host, 'Keep editing', fixture);

        expect((host.querySelector('#reset-u2') as HTMLInputElement).value).toBe(
          'a temporary one!',
        );
        expect(resetPassword).not.toHaveBeenCalled();
      });

      it('cancels without sending anything, and forgets what was typed', () => {
        const { fixture, host, resetPassword } = openReset();

        typeInto(host, fixture, 'a temporary one!');
        click(host, 'Cancel', fixture);
        click(host, 'Reset password…', fixture);

        expect((host.querySelector('#reset-u2') as HTMLInputElement).value).toBe('');
        expect(resetPassword).not.toHaveBeenCalled();
      });
    });

    describe('what the last action did', () => {
      it('says how many sessions a reset ended, and what the member must do next', () => {
        const { host } = renderMembers({
          notice: { kind: 'reset', email: 'bob@example.com', count: 2 },
        });

        const status = host.querySelector('[role="status"]')!.textContent!;

        expect(status).toContain('Reset the password for');
        expect(status).toContain('bob@example.com');
        expect(status).toContain('2 sessions were signed out');
        expect(status).toContain('choose a new password');
      });

      it('says how many credentials a disabling revoked, in the singular for one', () => {
        const one = renderMembers({
          notice: { kind: 'disabled', email: 'bob@example.com', count: 1 },
        });
        expect(one.host.querySelector('[role="status"]')!.textContent).toContain(
          '1 credential was revoked',
        );

        TestBed.resetTestingModule();

        const many = renderMembers({
          notice: { kind: 'disabled', email: 'bob@example.com', count: 3 },
        });
        expect(many.host.querySelector('[role="status"]')!.textContent).toContain(
          '3 credentials were revoked',
        );
      });

      it('says an enabling let them sign in again, and claims nothing came back', () => {
        const { host } = renderMembers({
          notice: { kind: 'enabled', email: 'bob@example.com', count: 0 },
        });

        const status = host.querySelector('[role="status"]')!.textContent!;

        expect(status).toContain('Enabled');
        expect(status).toContain('They can sign in again');
      });
    });
  });
});

function renderKeys(
  state: {
    keys?: readonly ApiKey[];
    loading?: boolean;
    isEmpty?: boolean;
    error?: ConcordatError | null;
    issuing?: boolean;
    issueError?: ConcordatError | null;
    issued?: IssuedApiKey | null;
    revoking?: readonly string[];
    revokeErrors?: Record<string, ConcordatError>;
  } = {},
  scopes: readonly Scope[] = ['org:admin', 'subject:read', 'subject:write', 'ci'],
) {
  const issue = vi.fn();
  const revoke = vi.fn();
  const dismiss = vi.fn();
  const issued = signal(state.issued ?? null);

  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });
  TestBed.overrideComponent(ApiKeysPage, {
    set: {
      providers: [
        {
          provide: ApiKeysStore,
          useValue: {
            keys: signal(state.keys ?? [apiKey()]),
            loading: signal(state.loading ?? false),
            isEmpty: signal(state.isEmpty ?? false),
            error: signal(state.error ?? null),
            issuing: signal(state.issuing ?? false),
            issueError: signal(state.issueError ?? null),
            issued,
            revoking: signal(state.revoking ?? []),
            revokeErrors: signal(state.revokeErrors ?? {}),
            load: () => {},
            issue,
            revoke,
            dismiss,
          },
        },
      ],
    },
  });
  TestBed.inject(SessionStore).signIn({ credential: 'c', actor: 'a', scopes });

  const fixture = TestBed.createComponent(ApiKeysPage);
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, issue, revoke, dismiss, issued };
}

function tick(host: HTMLElement, scope: string): void {
  const box = [...host.querySelectorAll('fieldset label')]
    .find((l) => l.textContent?.trim() === scope)
    ?.querySelector('input') as HTMLInputElement;
  box.click();
}

describe('ApiKeysPage', () => {
  it('lists each key with its label, scopes and key id — never a secret', () => {
    const { host } = renderKeys();

    expect(host.textContent).toContain('ci-pipeline');
    expect(host.textContent).toContain('abc123');
    expect(host.textContent).toContain('subject:write');
    expect(host.querySelector('input[readonly]')).toBeNull();
  });

  it('marks a revoked key and offers no way to revoke it again', () => {
    const { host } = renderKeys({
      keys: [apiKey({ revokedAt: new Date('2026-08-14T09:00:00Z') })],
    });

    expect(host.textContent).toContain('REVOKED');
    expect(button(host, 'Revoke')).toBeUndefined();
  });

  it('marks an expired key', () => {
    const { host } = renderKeys({
      keys: [apiKey({ expiresAt: new Date('2020-01-01T00:00:00Z') })],
    });

    expect(host.textContent).toContain('EXPIRED');
  });

  it('says a key that has never authenticated has not, and that one without an expiry has none', () => {
    const { host } = renderKeys();

    expect(host.textContent).toContain('never used');
    expect(host.textContent).toContain('does not expire');
  });

  it('says what an empty list means', () => {
    expect(renderKeys({ keys: [], isEmpty: true }).host.textContent).toContain('No API keys');
  });

  describe('the secret', () => {
    const issued: IssuedApiKey = { key: apiKey({ label: 'fresh' }), secret: 'cdt_abc_SECRET' };

    it('is shown once, in a field a person can copy from, with what it cannot be recovered from', () => {
      const { host } = renderKeys({ issued });

      const field = host.querySelector('input[readonly]') as HTMLInputElement;

      expect(field.value).toBe('cdt_abc_SECRET');
      expect(host.textContent).toContain('will not be shown again');
      expect(host.textContent).toContain('keeps only a hash');
    });

    it('says which key it belongs to', () => {
      expect(renderKeys({ issued }).host.textContent).toContain('fresh');
    });

    it('stays until dismissed, and dismissing clears it from the store', () => {
      const { fixture, host, dismiss, issued: issuedSignal } = renderKeys({ issued });

      button(host, 'I have saved it')!.click();

      expect(dismiss).toHaveBeenCalledTimes(1);

      issuedSignal.set(null);
      fixture.detectChanges();

      expect(host.querySelector('input[readonly]')).toBeNull();
    });
  });

  describe('issuing', () => {
    it('offers only the scopes the issuer holds', () => {
      const { host } = renderKeys({}, ['org:admin', 'subject:read']);

      const offered = [...host.querySelectorAll('fieldset label')].map((l) =>
        l.textContent?.trim(),
      );

      expect(offered).toEqual(['subject:read', 'org:admin']);
    });

    it('ticks none, so a least-privilege key is the one that takes no effort', () => {
      const { host } = renderKeys();

      const ticked = [...host.querySelectorAll<HTMLInputElement>('fieldset input:checked')];

      expect(ticked).toEqual([]);
      expect(button(host, 'Issue key')!.disabled).toBe(true);
    });

    it('sends the label and the ticked scopes in the registry’s own order', () => {
      const { fixture, host, issue } = renderKeys();

      fill(host, 'key-label', '  deploy  ');
      tick(host, 'ci');
      tick(host, 'subject:write');
      fixture.detectChanges();
      host.querySelector('form')!.dispatchEvent(new Event('submit'));

      expect(issue).toHaveBeenCalledWith({
        label: 'deploy',
        scopes: ['subject:write', 'ci'],
        expiresAt: null,
      });
    });

    it('treats an expiry date as the end of that local day', () => {
      const { fixture, host, issue } = renderKeys();

      fill(host, 'key-label', 'deploy');
      fill(host, 'key-expires', '2026-12-31');
      tick(host, 'subject:read');
      fixture.detectChanges();
      host.querySelector('form')!.dispatchEvent(new Event('submit'));

      expect(issue.mock.calls[0]![0].expiresAt).toEqual(new Date('2026-12-31T23:59:59.999'));
    });

    it('sends nothing without a label, or with no scope', () => {
      const { fixture, host, issue } = renderKeys();

      tick(host, 'subject:read');
      fixture.detectChanges();
      host.querySelector('form')!.dispatchEvent(new Event('submit'));
      expect(issue).not.toHaveBeenCalled();

      fill(host, 'key-label', 'deploy');
      tick(host, 'subject:read');
      fixture.detectChanges();
      host.querySelector('form')!.dispatchEvent(new Event('submit'));
      expect(issue).not.toHaveBeenCalled();
    });

    it('shows the registry’s refusal, such as asking for more than the issuer holds', () => {
      const { host } = renderKeys({
        issueError: new ConcordatError({
          status: 403,
          code: 'scope_escalation',
          detail: 'More than you hold.',
        }),
      });

      expect(host.textContent).toContain('Could not issue the key');
      expect(host.textContent).toContain('More than you hold.');
    });
  });

  describe('revoking', () => {
    it('takes two clicks, and names the key and what happens', () => {
      const { fixture, host, revoke } = renderKeys();

      button(host, 'Revoke')!.click();
      fixture.detectChanges();

      expect(revoke).not.toHaveBeenCalled();
      expect(host.textContent).toContain('Revoke ci-pipeline?');
      expect(host.textContent).toContain('stops working on its next request');

      button(host, 'Confirm')!.click();

      expect(revoke).toHaveBeenCalledWith('k1');
    });

    it('sends nothing when cancelled', () => {
      const { fixture, host, revoke } = renderKeys();

      button(host, 'Revoke')!.click();
      fixture.detectChanges();
      button(host, 'Cancel')!.click();
      fixture.detectChanges();

      expect(revoke).not.toHaveBeenCalled();
      expect(button(host, 'Revoke')).toBeDefined();
    });

    it('shows a refusal on the key', () => {
      const { host } = renderKeys({
        revokeErrors: {
          k1: new ConcordatError({ status: 404, code: 'x', detail: 'Already gone.' }),
        },
      });

      expect(host.textContent).toContain('Could not revoke the key');
      expect(host.textContent).toContain('Already gone.');
    });
  });
});
