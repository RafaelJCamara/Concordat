import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import type { AuditEntry, AuditQuery } from '../../../domain/governance/audit';
import { AuditApi } from './audit-api';

// The URL and the parameters are what this file pins: a wrong one filters silently, and an
// audit log filtered by accident is wrong in a way that reads as authoritative.

const everything: AuditQuery = {
  scope: 'all',
  action: null,
  target: '',
  since: null,
  until: null,
  limit: 100,
};

function setUp() {
  TestBed.configureTestingModule({
    providers: [
      provideConcordatConfig({ defaultEnvironment: 'dev' }),
      provideHttpClient(),
      provideHttpClientTesting(),
    ],
  });

  return { api: TestBed.inject(AuditApi), backend: TestBed.inject(HttpTestingController) };
}

describe('AuditApi', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('reads /v1/audit, which has no environment in its path', () => {
    const { api, backend } = setUp();
    api.getAudit('dev', everything).subscribe();

    backend.expectOne((r) => r.url === '/v1/audit');
  });

  it('sends only the limit when asked for everything everywhere', () => {
    const { api, backend } = setUp();
    api.getAudit('dev', everything).subscribe();

    const params = backend.expectOne((r) => r.url === '/v1/audit').request.params;

    expect(params.keys().sort()).toEqual(['limit']);
    expect(params.get('limit')).toBe('100');
  });

  it('scopes to the active environment by name when asked to', () => {
    const { api, backend } = setUp();
    api.getAudit('staging', { ...everything, scope: 'environment' }).subscribe();

    expect(backend.expectOne((r) => r.url === '/v1/audit').request.params.get('env')).toBe(
      'staging',
    );
  });

  it('does not send the environment when asked for every environment', () => {
    const { api, backend } = setUp();
    api.getAudit('staging', everything).subscribe();

    expect(backend.expectOne((r) => r.url === '/v1/audit').request.params.has('env')).toBe(false);
  });

  it('sends the action and a trimmed target', () => {
    const { api, backend } = setUp();
    api
      .getAudit('dev', { ...everything, action: 'VERSION_APPROVED', target: '  orders.created ' })
      .subscribe();

    const params = backend.expectOne((r) => r.url === '/v1/audit').request.params;

    expect(params.get('action')).toBe('VERSION_APPROVED');
    expect(params.get('target')).toBe('orders.created');
  });

  it('leaves a blank target off rather than sending an empty filter', () => {
    const { api, backend } = setUp();
    api.getAudit('dev', { ...everything, target: '   ' }).subscribe();

    expect(backend.expectOne((r) => r.url === '/v1/audit').request.params.has('target')).toBe(
      false,
    );
  });

  it('sends the range as UTC instants', () => {
    const { api, backend } = setUp();
    api
      .getAudit('dev', {
        ...everything,
        since: new Date('2026-08-03T00:00:00Z'),
        until: new Date('2026-08-05T23:59:59.999Z'),
      })
      .subscribe();

    const params = backend.expectOne((r) => r.url === '/v1/audit').request.params;

    expect(params.get('since')).toBe('2026-08-03T00:00:00.000Z');
    expect(params.get('until')).toBe('2026-08-05T23:59:59.999Z');
  });

  it('returns domain entries, with the timestamp parsed', () => {
    let entries: readonly AuditEntry[] = [];
    const { api, backend } = setUp();
    api.getAudit('dev', everything).subscribe((value) => (entries = value));

    backend
      .expectOne((r) => r.url === '/v1/audit')
      .flush([
        {
          at: '2026-08-13T09:00:00+00:00',
          action: 'VERSION_APPROVED',
          actor: 'alice',
          target: 'orders.created',
          detail: 'version 2',
        },
      ]);

    expect(entries[0]?.at).toBeInstanceOf(Date);
    expect(entries[0]?.at.toISOString()).toBe('2026-08-13T09:00:00.000Z');
  });
});
