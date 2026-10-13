import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import type { ImpactReport } from '../../../domain/registry/impact';
import { ImpactApi } from './impact-api';
import type { ImpactDto } from './impact-dtos';

const dto: ImpactDto = {
  subject: 'orders.created',
  candidateOrdinal: 2,
  candidateSchemaId: 'abc',
  breakingCount: 0,
  consumers: [],
  contracts: [],
};

function setUp() {
  TestBed.configureTestingModule({
    providers: [
      provideConcordatConfig({ defaultEnvironment: 'dev' }),
      provideHttpClient(),
      provideHttpClientTesting(),
    ],
  });

  return { api: TestBed.inject(ImpactApi), backend: TestBed.inject(HttpTestingController) };
}

describe('ImpactApi', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('asks for a specific version with the `version` query parameter', () => {
    const { api, backend } = setUp();
    api.getImpact('dev', 'orders.created', 2).subscribe();

    const request = backend.expectOne(
      (r) => r.url === '/v1/environments/dev/subjects/orders.created/impact',
    );

    expect(request.request.params.get('version')).toBe('2');
  });

  it('omits the parameter for the latest, so the server resolves it', () => {
    // Sending the word `latest` would be a 400: the parameter is an integer. The report says
    // which ordinal the server chose, so nothing is lost by not naming one.
    const { api, backend } = setUp();
    api.getImpact('dev', 'orders.created', null).subscribe();

    const request = backend.expectOne(
      (r) => r.url === '/v1/environments/dev/subjects/orders.created/impact',
    );

    expect(request.request.params.has('version')).toBe(false);
  });

  it('percent-encodes the subject name', () => {
    const { api, backend } = setUp();
    api.getImpact('dev', 'orders/created', 1).subscribe();

    backend.expectOne((r) => r.url === '/v1/environments/dev/subjects/orders%2Fcreated/impact');
  });

  it('returns a domain report', () => {
    let report: ImpactReport | null = null;
    const { api, backend } = setUp();
    api.getImpact('dev', 'orders.created', 2).subscribe((value) => (report = value));

    backend.expectOne((r) => r.url.endsWith('/impact')).flush(dto);

    expect(report!.candidateOrdinal).toBe(2);
  });
});
