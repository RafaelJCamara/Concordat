import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { type ConcordatConfig, provideConcordatConfig } from '../config/app-config';
import { TENANT_HEADER, tenantInterceptor } from './tenant-interceptor';

// The header is a selector among the tenants a caller is already entitled to, never the
// tenant itself. That makes two of the three cases below about *not* sending it.

const API = '/v1/environments/dev/subjects';
const ELSEWHERE = 'https://cdn.example.com/avatars/ci.png';

function setUp(config: Partial<ConcordatConfig>) {
  TestBed.configureTestingModule({
    providers: [
      provideConcordatConfig(config),
      provideHttpClient(withInterceptors([tenantInterceptor])),
      provideHttpClientTesting(),
    ],
  });

  return {
    http: TestBed.inject(HttpClient),
    backend: TestBed.inject(HttpTestingController),
  };
}

describe('tenantInterceptor', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('names the tenant when the deployment has more than one', () => {
    const { http, backend } = setUp({ profile: 'cloud', tenant: 'acme' });
    http.get(API).subscribe();

    expect(backend.expectOne(API).request.headers.get(TENANT_HEADER)).toBe('acme');
  });

  it('sends nothing when the deployment has a single implicit tenant', () => {
    // Self-hosted binds `TenantId.Default` server-side, so there is nothing for the client
    // to say. Sending a guessed value would be a claim the client is not entitled to make,
    // and it would start failing silently the day the server begins checking it.
    const { http, backend } = setUp({});
    http.get(API).subscribe();

    expect(backend.expectOne(API).request.headers.has(TENANT_HEADER)).toBe(false);
  });

  it('does not tell a third-party host which tenant this is', () => {
    // The tenant name is not a secret, but it is not a CDN's business either, and the gate
    // that stops it leaking is the same one that stops the credential leaking.
    const { http, backend } = setUp({ profile: 'cloud', tenant: 'acme' });
    http.get(ELSEWHERE).subscribe();

    expect(backend.expectOne(ELSEWHERE).request.headers.has(TENANT_HEADER)).toBe(false);
  });
});

describe('TENANT_HEADER', () => {
  it('is spelled the way the server will be asked to read it', () => {
    // A rename here is invisible until an authorisation test in M8 fails for a reason that
    // looks nothing like a header name. The name still needs agreeing with M8 — see
    // NOTES-FOR-INTEGRATION §3.5 — so this pins today's guess, not a settled contract.
    expect(TENANT_HEADER).toBe('X-Concordat-Tenant');
  });
});
