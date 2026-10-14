import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import type { EnvironmentSummary } from '../../../domain/registry/environment-summary';
import { SummaryApi } from './summary-api';
import type { EnvironmentSummaryDto } from './summary-dtos';

// The only place that asks the registry for the overview. What is worth pinning is the address,
// because a wrong one 404s in a way that reads as an empty environment, and that callers are
// handed domain types with their timestamps already parsed.

const dto: EnvironmentSummaryDto = {
  subjects: { total: 1, active: 1, deprecated: 0, retired: 0 },
  versions: { total: 2, active: 1, awaitingApproval: 1, rejected: 0, dismissed: 0 },
  newestSubjects: [],
  breakingChanges: [
    {
      subject: 'orders.created',
      ordinal: 2,
      status: 'AWAITING_APPROVAL',
      semanticVersion: null,
      registeredAt: '2026-10-14T09:00:00+00:00',
      registeredBy: 'ci',
      decidedAt: null,
      decidedBy: null,
    },
  ],
  enforcement: [
    { mode: 'OFF', contracts: 0, publishBindings: 0, consumeBindings: 0 },
    { mode: 'MONITOR', contracts: 0, publishBindings: 0, consumeBindings: 0 },
    { mode: 'ENFORCE', contracts: 0, publishBindings: 0, consumeBindings: 0 },
  ],
  violations: { distinct: 0, recent: 0, windowHours: 24 },
};

function setUp() {
  TestBed.configureTestingModule({
    providers: [provideConcordatConfig(), provideHttpClient(), provideHttpClientTesting()],
  });

  return { api: TestBed.inject(SummaryApi), backend: TestBed.inject(HttpTestingController) };
}

describe('SummaryApi', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('asks the environment for its summary, under the version prefix', () => {
    const { api, backend } = setUp();

    api.getSummary('dev').subscribe();

    backend.expectOne({ method: 'GET', url: '/v1/environments/dev/summary' });
  });

  it('percent-encodes the environment name', () => {
    // An environment name is typed by a person. One with a slash in it must not address a
    // different route.
    const { api, backend } = setUp();

    api.getSummary('a/b').subscribe();

    backend.expectOne('/v1/environments/a%2Fb/summary');
  });

  it('hands back domain types, with timestamps already parsed', () => {
    const { api, backend } = setUp();
    let summary: EnvironmentSummary | undefined;
    api.getSummary('dev').subscribe((value) => (summary = value));

    backend.expectOne('/v1/environments/dev/summary').flush(dto);

    expect(summary?.breakingChanges[0]?.registeredAt).toBeInstanceOf(Date);
    expect(summary?.breakingChanges[0]?.registeredAt.toISOString()).toBe(
      '2026-10-14T09:00:00.000Z',
    );
  });
});
