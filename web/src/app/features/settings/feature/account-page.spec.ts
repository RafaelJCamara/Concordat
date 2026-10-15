import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';
import { SessionStore } from '../../../core/auth/session-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { Scope } from '../../../domain/identity/scope';
import { AccountStore } from '../application/account-store';
import { AccountPage } from './account-page';

// The screen's own decisions: that both passwords are asked for and never shown, what blocks sending
// and what does not, that the second click is the one that sends, that the passwords are cleared
// the moment they go, and that a session which must change its password is told so, is shown nothing
// else, and is sent on once it has.

function render(
  options: {
    forced?: boolean;
    actor?: string;
    scopes?: readonly Scope[];
    submitting?: boolean;
    error?: ConcordatError | null;
    changed?: { otherSessionsSignedOut: number } | null;
  } = {},
) {
  const change = vi.fn();
  const changed = signal(options.changed ?? null);

  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });
  TestBed.overrideComponent(AccountPage, {
    set: {
      providers: [
        {
          provide: AccountStore,
          useValue: {
            submitting: signal(options.submitting ?? false),
            error: signal(options.error ?? null),
            changed,
            change,
          },
        },
      ],
    },
  });

  TestBed.inject(SessionStore).signIn({
    credential: 'c',
    actor: options.actor ?? 'alice@example.com',
    scopes: options.scopes ?? ['subject:read'],
    mustChangePassword: options.forced ?? false,
  });

  const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(AccountPage);
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, change, changed, navigate };
}

function fill(host: HTMLElement, id: string, value: string): void {
  const element = host.querySelector(`#${id}`) as HTMLInputElement;
  element.value = value;
  element.dispatchEvent(new Event('input'));
}

function button(host: HTMLElement, text: string): HTMLButtonElement | undefined {
  return [...host.querySelectorAll('button')].find((b) => b.textContent?.trim() === text);
}

function fillAll(
  host: HTMLElement,
  fixture: { detectChanges(): void },
  values: { current?: string; next?: string; confirmation?: string } = {},
): void {
  fill(host, 'account-current', values.current ?? 'the current one');
  fill(host, 'account-new', values.next ?? 'a brand new one');
  fill(host, 'account-confirm', values.confirmation ?? values.next ?? 'a brand new one');
  fixture.detectChanges();
}

describe('AccountPage', () => {
  describe('the form', () => {
    it('asks for the current password and the new one twice, none of them shown or offered to a password manager as the same', () => {
      const { host } = render();

      const current = host.querySelector('#account-current') as HTMLInputElement;
      const next = host.querySelector('#account-new') as HTMLInputElement;
      const confirmation = host.querySelector('#account-confirm') as HTMLInputElement;

      expect([current.type, next.type, confirmation.type]).toEqual([
        'password',
        'password',
        'password',
      ]);
      expect(current.getAttribute('autocomplete')).toBe('current-password');
      expect(next.getAttribute('autocomplete')).toBe('new-password');
      expect(confirmation.getAttribute('autocomplete')).toBe('new-password');
    });

    it('says the rule, and that there is no other', () => {
      expect(render().host.textContent).toContain('At least 12 characters, and no other rules');
    });

    it('sends nothing until all three are filled in', () => {
      const { fixture, host } = render();

      expect(button(host, 'Review change')!.disabled).toBe(true);

      fillAll(host, fixture, { current: '' });

      expect(button(host, 'Review change')!.disabled).toBe(true);
    });

    it('blocks a new password under twelve characters, and says why', () => {
      const { fixture, host } = render();

      fillAll(host, fixture, { next: 'too short' });

      expect(button(host, 'Review change')!.disabled).toBe(true);
      expect(host.textContent).toContain('at least 12 characters');
    });

    it('blocks two new passwords that differ, which is a typo and not a request', () => {
      const { fixture, host } = render();

      fillAll(host, fixture, { next: 'a brand new one', confirmation: 'a brand new ont' });

      expect(button(host, 'Review change')!.disabled).toBe(true);
      expect(host.textContent).toContain('not the same');
    });

    it('blocks the password the person already has, as the registry would', () => {
      const { fixture, host } = render();

      fillAll(host, fixture, { current: 'the same one!!', next: 'the same one!!' });

      expect(button(host, 'Review change')!.disabled).toBe(true);
      expect(host.textContent).toContain('already have');
    });

    it('says nothing about a field nobody has started on', () => {
      const { host } = render();

      expect(host.querySelector('#account-finding')?.textContent?.trim()).toBe('');
    });
  });

  describe('asking twice', () => {
    it('sends nothing on the first click, only says what will happen', () => {
      const { fixture, host, change } = render();

      fillAll(host, fixture);
      button(host, 'Review change')!.click();
      fixture.detectChanges();

      expect(change).not.toHaveBeenCalled();
      expect(host.textContent).toContain('Change the password for alice@example.com?');
      expect(host.textContent).toContain('Every other session of yours is signed out');
      expect(host.textContent).toContain('This one stays signed in');
      expect(host.textContent).toContain('API key you issued keeps working');
      expect(host.textContent).toContain('PASSWORD_CHANGED');
      expect(host.textContent).toContain('Neither password is recorded');
    });

    it('sends both passwords on the second click, and clears the fields at once', () => {
      const { fixture, host, change } = render();

      fillAll(host, fixture);
      button(host, 'Review change')!.click();
      fixture.detectChanges();
      button(host, 'Yes, change my password')!.click();
      fixture.detectChanges();

      expect(change).toHaveBeenCalledWith({ current: 'the current one', next: 'a brand new one' });
      for (const id of ['account-current', 'account-new', 'account-confirm']) {
        expect((host.querySelector(`#${id}`) as HTMLInputElement).value).toBe('');
      }
    });

    it('goes back to the form from the review with nothing sent and what was typed kept', () => {
      const { fixture, host, change } = render();

      fillAll(host, fixture);
      button(host, 'Review change')!.click();
      fixture.detectChanges();
      button(host, 'Keep editing')!.click();
      fixture.detectChanges();

      expect(change).not.toHaveBeenCalled();
      expect((host.querySelector('#account-new') as HTMLInputElement).value).toBe(
        'a brand new one',
      );
    });

    it('does not offer a second click while the registry has not answered the first', () => {
      const { fixture, host } = render({ submitting: true });

      fillAll(host, fixture);

      expect(button(host, 'Review change')!.disabled).toBe(true);
    });
  });

  describe('what the registry answered', () => {
    it('shows a refusal as the registry said it, above the form', () => {
      const { host } = render({
        error: new ConcordatError({
          status: 401,
          code: 'unauthenticated',
          detail: 'That is not your current password.',
        }),
      });

      expect(host.textContent).toContain('The registry did not change it');
      expect(host.textContent).toContain('That is not your current password.');
    });

    it('says how many other sessions it signed out, from its answer', () => {
      const many = render({ changed: { otherSessionsSignedOut: 2 } });
      expect(many.host.querySelector('[role="status"]')!.textContent).toContain(
        '2 other sessions were signed out',
      );

      TestBed.resetTestingModule();

      const none = render({ changed: { otherSessionsSignedOut: 0 } });
      expect(none.host.querySelector('[role="status"]')!.textContent).toContain(
        'no other session to sign out',
      );
    });
  });

  describe('for anyone who is signed in', () => {
    it('is drawn for a reader, with the settings tabs, and no scope asked of them', () => {
      const { host } = render({ scopes: ['subject:read'] });

      expect(host.querySelector('h1')?.textContent).toContain('Settings');
      expect(host.querySelector('nav[aria-label="Settings"]')).not.toBeNull();
      expect(host.querySelector('form')).not.toBeNull();
    });
  });

  describe('a session that must replace a temporary password (ADR-048)', () => {
    it('says so first, and why, in words a person can act on', () => {
      const { host } = render({ forced: true, scopes: [] });

      expect(host.querySelector('h1')?.textContent).toContain('Choose a new password');
      expect(host.querySelector('[role="alert"]')?.textContent).toContain(
        'An administrator reset your password',
      );
      expect(host.textContent).toContain('the administrator who set it knows it');
    });

    it('is not shown the settings tabs, each of which would answer 403 to everything', () => {
      const { host } = render({ forced: true, scopes: [] });

      expect(host.querySelector('nav[aria-label="Settings"]')).toBeNull();
    });

    it('asks to replace the temporary one, and says what that does', () => {
      const { fixture, host } = render({ forced: true, scopes: [] });

      fillAll(host, fixture);
      button(host, 'Review change')!.click();
      fixture.detectChanges();

      expect(host.textContent).toContain('Replace the temporary password for alice@example.com?');
      expect(host.textContent).toContain('The temporary password stops working at once');
      expect(host.textContent).toContain('can do everything your role allows');
      expect(button(host, 'Yes, replace it')).toBeDefined();
    });

    it('sends the person on to the app once the password has been replaced', () => {
      const { fixture, changed, navigate } = render({ forced: true, scopes: [] });

      expect(navigate).not.toHaveBeenCalled();

      changed.set({ otherSessionsSignedOut: 0 });
      fixture.detectChanges();

      expect(navigate).toHaveBeenCalledWith('/');
    });

    it('does not send anyone anywhere after an ordinary change, which has nothing to leave', () => {
      const { fixture, changed, navigate } = render({ forced: false });

      changed.set({ otherSessionsSignedOut: 1 });
      fixture.detectChanges();

      expect(navigate).not.toHaveBeenCalled();
    });
  });
});
