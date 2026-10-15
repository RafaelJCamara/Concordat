import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import type { ApprovalQueue } from '../../../domain/registry/approval-queue';
import { ApprovalsApi } from './approvals-api';
import type { ApprovalQueueDto } from './approvals-dtos';

// The only place that asks the registry for the queue. What is worth pinning is the address,
// because a wrong one 404s in a way that reads as an empty queue, which is the one answer this
// screen must never give by accident; that a page is sent only when one was asked for; and that
// callers are handed domain types with their timestamps already parsed.

const dto: ApprovalQueueDto = {
  total: 7,
  items: [
    {
      subject: 'orders.created',
      ordinal: 2,
      semanticVersion: '2.0.0',
      changelog: 'required id',
      registeredAt: '2026-10-14T09:00:00+00:00',
      registeredBy: 'ci',
      baseline: 1,
      reviewers: [],
      mayDecide: true,
    },
  ],
};

function setUp() {
  TestBed.configureTestingModule({
    providers: [provideConcordatConfig(), provideHttpClient(), provideHttpClientTesting()],
  });

  return { api: TestBed.inject(ApprovalsApi), backend: TestBed.inject(HttpTestingController) };
}

describe('ApprovalsApi', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('asks the environment for its approvals, under the version prefix', () => {
    const { api, backend } = setUp();

    api.getQueue('dev').subscribe();

    backend.expectOne({ method: 'GET', url: '/v1/environments/dev/approvals' });
  });

  it('sends no query string when no page was asked for', () => {
    const { api, backend } = setUp();

    api.getQueue('dev').subscribe();

    expect(backend.expectOne('/v1/environments/dev/approvals').request.params.keys()).toEqual([]);
  });

  it('sends the page it was asked for', () => {
    const { api, backend } = setUp();

    api.getQueue('dev', { limit: 50, offset: 100 }).subscribe();

    const request = backend.expectOne(
      (r) => r.url === '/v1/environments/dev/approvals' && r.method === 'GET',
    ).request;

    expect(request.params.get('limit')).toBe('50');
    expect(request.params.get('offset')).toBe('100');
  });

  it('sends an offset of zero, which is a page and not an absent one', () => {
    const { api, backend } = setUp();

    api.getQueue('dev', { offset: 0 }).subscribe();

    const request = backend.expectOne((r) => r.url === '/v1/environments/dev/approvals').request;

    expect(request.params.get('offset')).toBe('0');
  });

  it('percent-encodes the environment name', () => {
    // An environment name is typed by a person. One with a slash in it must not address a
    // different route.
    const { api, backend } = setUp();

    api.getQueue('a/b').subscribe();

    backend.expectOne('/v1/environments/a%2Fb/approvals');
  });

  it('hands back domain types, with timestamps already parsed', () => {
    const { api, backend } = setUp();
    let queue: ApprovalQueue | undefined;
    api.getQueue('dev').subscribe((value) => (queue = value));

    backend.expectOne('/v1/environments/dev/approvals').flush(dto);

    expect(queue?.total).toBe(7);
    expect(queue?.items[0]?.registeredAt).toBeInstanceOf(Date);
    expect(queue?.items[0]?.registeredAt.toISOString()).toBe('2026-10-14T09:00:00.000Z');
    expect(queue?.items[0]?.baseline).toBe(1);
  });
});
