import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { AuthApi } from './auth-api';

describe('AuthApi', () => {
  let api: AuthApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideConcordatConfig()],
    });

    api = TestBed.inject(AuthApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('asks for the instance status without a body', () => {
    api.status().subscribe();

    const request = http.expectOne('/v1/auth/status');
    expect(request.request.method).toBe('GET');
    request.flush({ claimed: true, authenticated: false, actor: null, scopes: [] });
  });

  it('drops a scope this build does not know', () => {
    // A newer server can grant something this bundle has never heard of. Passing it through
    // would put an unrecognised string into a set compared against a closed union, and the
    // comparison would silently never match. Dropping it hides the affordance, which is the
    // safe direction — and the server remains the authority either way.
    let scopes: readonly string[] = [];
    api.status().subscribe((status) => (scopes = status.scopes));

    http.expectOne('/v1/auth/status').flush({
      claimed: true,
      authenticated: true,
      actor: 'ops',
      scopes: ['subject:read', 'quantum:entangle', 'org:admin'],
    });

    expect(scopes).toEqual(['subject:read', 'org:admin']);
  });

  it('exchanges an email and password for a credential', () => {
    let credential: string | null = null;
    let expires: Date | null = null;

    api.signIn('ops@example.com', 'a long password').subscribe((result) => {
      credential = result.credential;
      expires = result.expiresAt;
    });

    const request = http.expectOne('/v1/auth/signin');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      email: 'ops@example.com',
      password: 'a long password',
    });

    request.flush({
      credential: 'cdt_abc_def',
      actor: 'ops@example.com',
      scopes: ['subject:admin'],
      expiresAt: '2026-08-15T00:00:00Z',
    });

    expect(credential).toBe('cdt_abc_def');

    // Parsed to a Date at the boundary. A caller that received the raw string would compare
    // it as text somewhere, and the first timezone offset would make that wrong.
    expect(expires).toBeInstanceOf(Date);
  });

  it('sends a null display name rather than omitting it when bootstrapping', () => {
    api.bootstrap('owner@example.com', 'a long password').subscribe();

    const request = http.expectOne('/v1/auth/bootstrap');
    expect(request.request.body).toEqual({
      email: 'owner@example.com',
      password: 'a long password',
      displayName: null,
    });

    request.flush({});
  });
});
