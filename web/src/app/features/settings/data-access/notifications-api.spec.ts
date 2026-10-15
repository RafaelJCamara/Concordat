import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import type { CreatedSubscription, Subscription } from '../../../domain/notifications/subscription';
import { NotificationsApi, toSubscription, type SubscriptionDto } from './notifications-api';

const dto: SubscriptionDto = {
  id: '33333333-3333-3333-3333-333333333333',
  channel: 'WEBHOOK',
  endpoint: 'https://hooks.example.com/x',
  events: ['VERSION_REGISTERED'],
  enabled: true,
  createdAt: '2026-08-13T09:00:00+00:00',
};

function setUp() {
  TestBed.configureTestingModule({
    providers: [
      provideConcordatConfig({ defaultEnvironment: 'dev' }),
      provideHttpClient(),
      provideHttpClientTesting(),
    ],
  });

  return {
    api: TestBed.inject(NotificationsApi),
    backend: TestBed.inject(HttpTestingController),
  };
}

describe('NotificationsApi', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('lists an environment’s subscriptions', () => {
    let subscriptions: readonly Subscription[] = [];
    const { api, backend } = setUp();
    api.listSubscriptions('dev').subscribe((value) => (subscriptions = value));

    backend.expectOne('/v1/environments/dev/notifications').flush([dto]);

    expect(subscriptions[0]?.createdAt).toBeInstanceOf(Date);
  });

  it('sends an empty event list as empty, which the registry reads as every event', () => {
    // Left off would be a different request; empty is the registry's own spelling of "all".
    const { api, backend } = setUp();
    api.createSubscription('dev', { channel: 'EMAIL', endpoint: 'a@b.c', events: [] }).subscribe();

    expect(backend.expectOne('/v1/environments/dev/notifications').request.body).toEqual({
      channel: 'EMAIL',
      endpoint: 'a@b.c',
      events: [],
    });
  });

  it('returns a webhook’s signing secret from the create response only', () => {
    let created: CreatedSubscription | null = null;
    const { api, backend } = setUp();
    api
      .createSubscription('dev', { channel: 'WEBHOOK', endpoint: 'https://x', events: [] })
      .subscribe((value) => (created = value));

    backend
      .expectOne('/v1/environments/dev/notifications')
      .flush({ subscription: dto, signingSecret: 'whsec_abc' });

    expect(created!.signingSecret).toBe('whsec_abc');
    // And not on the subscription, which is what a list would hold.
    expect(Object.keys(created!.subscription)).not.toContain('signingSecret');
  });

  it('returns no secret for an email subscription', () => {
    let created: CreatedSubscription | null = null;
    const { api, backend } = setUp();
    api
      .createSubscription('dev', { channel: 'EMAIL', endpoint: 'a@b.c', events: [] })
      .subscribe((value) => (created = value));

    backend
      .expectOne('/v1/environments/dev/notifications')
      .flush({ subscription: { ...dto, channel: 'EMAIL' }, signingSecret: null });

    expect(created!.signingSecret).toBeNull();
  });

  it('mutes with a PUT to the subscription’s enabled route', () => {
    const { api, backend } = setUp();
    api.setEnabled('dev', dto.id, false).subscribe();

    const request = backend.expectOne(`/v1/environments/dev/notifications/${dto.id}/enabled`);

    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ enabled: false });
  });

  it('changes events with a PUT to the subscription’s events route, and nothing else in the body', () => {
    // Not the endpoint and not a secret: the point is to keep both.
    const { api, backend } = setUp();
    api.setEvents('dev', dto.id, ['VERSION_PROMOTED', 'SUBJECT_DEPRECATED']).subscribe();

    const request = backend.expectOne(`/v1/environments/dev/notifications/${dto.id}/events`);

    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ events: ['VERSION_PROMOTED', 'SUBJECT_DEPRECATED'] });
    expect(Object.keys(request.request.body as object)).toEqual(['events']);
  });

  it('sends an empty list as empty, because the registry refuses a body without the member', () => {
    // An empty list is every event; an absent member is refused so a typo cannot widen who is told.
    const { api, backend } = setUp();
    api.setEvents('dev', dto.id, []).subscribe();

    expect(
      backend.expectOne(`/v1/environments/dev/notifications/${dto.id}/events`).request.body,
    ).toEqual({ events: [] });
  });

  it('hands back the subscription the registry answered with, as a domain type', () => {
    let changed: Subscription | null = null;
    const { api, backend } = setUp();
    api.setEvents('dev', dto.id, ['VERSION_PROMOTED']).subscribe((value) => (changed = value));

    backend
      .expectOne(`/v1/environments/dev/notifications/${dto.id}/events`)
      .flush({ ...dto, events: ['VERSION_PROMOTED'] });

    expect((changed as Subscription | null)?.events).toEqual(['VERSION_PROMOTED']);
    expect((changed as Subscription | null)?.createdAt).toBeInstanceOf(Date);
  });
  it('deletes, and a 204 with no body is a success', () => {
    let done = false;
    const { api, backend } = setUp();
    api.deleteSubscription('dev', dto.id).subscribe(() => (done = true));

    backend
      .expectOne(`/v1/environments/dev/notifications/${dto.id}`)
      .flush(null, { status: 204, statusText: 'No Content' });

    expect(done).toBe(true);
  });

  it('reads the outbox depth from the deployment-wide route', () => {
    let depth: { pending: number; parked: number } | null = null;
    const { api, backend } = setUp();
    api.outboxDepth().subscribe((value) => (depth = value));

    backend.expectOne('/v1/notifications/outbox').flush({ pending: 2, parked: 1 });

    expect(depth).toEqual({ pending: 2, parked: 1 });
  });
});

describe('toSubscription', () => {
  it.each(['EMAIL', 'WEBHOOK'])('accepts the channel %s', (channel) => {
    expect(toSubscription({ ...dto, channel }).channel).toBe(channel);
  });

  it('refuses a channel it does not recognise', () => {
    expect(() => toSubscription({ ...dto, channel: 'CARRIER_PIGEON' })).toThrow(/CARRIER_PIGEON/);
  });

  it('keeps an event it has never heard of, as received', () => {
    // Configuration being shown back: a subscription this build cannot display is exactly the
    // one somebody opened the screen to check.
    expect(toSubscription({ ...dto, events: ['TENANT_SUSPENDED'] }).events).toEqual([
      'TENANT_SUSPENDED',
    ]);
  });
});
