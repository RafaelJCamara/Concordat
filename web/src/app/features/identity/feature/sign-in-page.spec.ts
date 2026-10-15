import { signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { SessionStore } from '../../../core/auth/session-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import { SignInStore } from '../application/sign-in-store';
import { SignInPage } from './sign-in-page';

// The page's own logic is which of two screens it is (setup or sign-in), what it says when the registry
// refuses, and what it sends. Signing in and claiming are the store's and the registry's.

function render(options: { claimed: boolean; error?: ConcordatError | null }): {
  fixture: ComponentFixture<SignInPage>;
  host: HTMLElement;
  claims: unknown[];
} {
  const claims: unknown[] = [];
  const error = options.error ?? null;

  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });
  TestBed.overrideComponent(SignInPage, {
    set: {
      providers: [
        {
          provide: SignInStore,
          useValue: {
            submitting: signal(false),
            succeeded: signal(false),
            error: signal(error),
            message: signal(error?.detail ?? null),
            bootstrap: (request: unknown) => claims.push(request),
            signIn: () => {},
          },
        },
      ],
    },
  });
  TestBed.inject(SessionStore).observeInstance({
    claimed: options.claimed,
    actor: null,
    scopes: [],
  });

  const fixture = TestBed.createComponent(SignInPage);
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, claims };
}

const refusal = (status: number, detail: string) =>
  new ConcordatError({ status, code: 'unauthenticated', detail });

describe('SignInPage', () => {
  it('is the setup screen while nobody has an account, with a setup token box', () => {
    const { host } = render({ claimed: false });

    expect(host.querySelector('h1')?.textContent).toContain('Set up Concordat');
    expect(host.querySelector('#setup-token')).not.toBeNull();
    expect(host.textContent).toContain('Create owner account');
  });

  it('is a sign-in once there is an account, and offers no setup token', () => {
    const { host } = render({ claimed: true });

    expect(host.querySelector('h1')?.textContent).toContain('Sign in');
    expect(host.querySelector('#setup-token')).toBeNull();
  });

  it('sends the token with the claim, trimmed, and nothing else new', () => {
    const { fixture, host, claims } = render({ claimed: false });

    const set = (selector: string, value: string) => {
      const input = host.querySelector<HTMLInputElement>(selector)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    };
    set('#email', 'owner@example.com');
    set('#password', 'a long first password');
    set('#setup-token', '  s3cret  ');
    fixture.detectChanges();

    host.querySelector('form')!.dispatchEvent(new Event('submit'));

    expect(claims).toEqual([
      { email: 'owner@example.com', password: 'a long first password', token: 's3cret' },
    ]);
  });

  it('sends an empty token when the box is empty, which the API client leaves out', () => {
    const { fixture, host, claims } = render({ claimed: false });

    const set = (selector: string, value: string) => {
      const input = host.querySelector<HTMLInputElement>(selector)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    };
    set('#email', 'owner@example.com');
    set('#password', 'a long first password');
    fixture.detectChanges();

    host.querySelector('form')!.dispatchEvent(new Event('submit'));

    expect(claims).toEqual([
      { email: 'owner@example.com', password: 'a long first password', token: '' },
    ]);
  });

  it('says what a refused claim needs, in words for a person and not for a request', () => {
    const { host } = render({
      claimed: false,
      error: refusal(401, "A bootstrap token is required. Send it as 'token' in the request body."),
    });

    const alert = host.querySelector('[role="alert"], [hlmAlert]')!;
    expect(alert.textContent).toContain('asks for a setup token');
    expect(alert.textContent).toContain('CONCORDAT_BOOTSTRAP_TOKEN');
    expect(alert.textContent).not.toContain('request body');
  });

  it('shows any other refusal of a claim in the registry’s own words', () => {
    const { host } = render({
      claimed: false,
      error: refusal(400, 'The password must be at least 12 characters.'),
    });

    expect(host.textContent).toContain('The password must be at least 12 characters.');
    expect(host.textContent).not.toContain('asks for a setup token');
  });

  it('does not reword a refused sign-in: the registry says one thing for every reason, on purpose', () => {
    const { host } = render({
      claimed: true,
      error: refusal(401, 'The email or password is not right.'),
    });

    expect(host.textContent).toContain('The email or password is not right.');
    expect(host.textContent).not.toContain('setup token');
  });
});
