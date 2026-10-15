import { TestBed } from '@angular/core/testing';
import { of, Subject as ResponseStream, throwError } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type {
  CreatedSubscription,
  OutboxDepth,
  Subscription,
} from '../../../domain/notifications/subscription';
import { NotificationsApi, type NewSubscription } from '../data-access/notifications-api';
import { NotificationsStore } from './notifications-store';

function subscription(id: string, overrides: Partial<Subscription> = {}): Subscription {
  return {
    id,
    channel: 'WEBHOOK',
    endpoint: `https://hooks.example.com/${id}`,
    events: [],
    enabled: true,
    createdAt: new Date('2026-08-13T09:00:00Z'),
    ...overrides,
  };
}

function fakeApi(outbox: () => ReturnType<NotificationsApi['outboxDepth']>) {
  const calls: { kind: string; args: unknown[]; stream: ResponseStream<unknown> }[] = [];

  function call<T>(kind: string, ...args: unknown[]) {
    const stream = new ResponseStream<T>();
    calls.push({ kind, args, stream: stream as ResponseStream<unknown> });
    return stream.asObservable();
  }

  return {
    calls,
    ofKind: (kind: string) => calls.filter((c) => c.kind === kind),
    listSubscriptions: () => call<readonly Subscription[]>('list'),
    outboxDepth: outbox,
    createSubscription: (_env: string, s: NewSubscription) =>
      call<CreatedSubscription>('create', s),
    setEnabled: (_env: string, id: string, enabled: boolean) =>
      call<Subscription>('enabled', id, enabled),
    setEvents: (_env: string, id: string, events: readonly string[]) =>
      call<Subscription>('events', id, events),
    deleteSubscription: (_env: string, id: string) => call<void>('delete', id),
  };
}

describe('NotificationsStore', () => {
  let api: ReturnType<typeof fakeApi>;
  let store: InstanceType<typeof NotificationsStore>;

  function setUp(
    outbox: () => ReturnType<NotificationsApi['outboxDepth']> = () => of({ pending: 0, parked: 0 }),
  ) {
    // A test that wants a different outbox calls this again, so the module it is replacing
    // has to be thrown away first or the original fake keeps answering.
    TestBed.resetTestingModule();
    api = fakeApi(outbox);

    TestBed.configureTestingModule({
      providers: [
        provideConcordatConfig({ defaultEnvironment: 'dev' }),
        { provide: NotificationsApi, useValue: api },
        NotificationsStore,
      ],
    });

    store = TestBed.inject(NotificationsStore);
  }

  function loaded(subs: readonly Subscription[] = [subscription('a')]): void {
    store.load();
    api.ofKind('list')[0]!.stream.next(subs);
    api.ofKind('list')[0]!.stream.complete();
  }

  beforeEach(() => setUp());

  describe('changing a subscription’s events', () => {
    it('asks the registry in the active environment, for that subscription, with exactly the events given', () => {
      loaded();

      store.setEvents({ id: 'a', events: ['VERSION_PROMOTED'] });

      expect(api.ofKind('events')[0]?.args).toEqual(['a', ['VERSION_PROMOTED']]);
      expect(store.busy()).toEqual(['a']);
    });

    it('sends an empty list as empty, which is every event', () => {
      loaded([subscription('a', { events: ['VERSION_PROMOTED'] })]);

      store.setEvents({ id: 'a', events: [] });

      expect(api.ofKind('events')[0]?.args).toEqual(['a', []]);
    });

    it('shows what the registry answered, and no more than that row changes', () => {
      loaded([subscription('a'), subscription('b')]);

      store.setEvents({ id: 'a', events: ['VERSION_PROMOTED', 'VERSION_PROMOTED'] });
      api.ofKind('events')[0]!.stream.next(subscription('a', { events: ['VERSION_PROMOTED'] }));

      expect(store.subscriptions().map((s) => s.events)).toEqual([['VERSION_PROMOTED'], []]);
      expect(store.busy()).toEqual([]);
    });

    it('keeps a refusal on the row, and the subscription as it was', () => {
      loaded([subscription('a', { events: ['VERSION_PROMOTED'] })]);

      store.setEvents({ id: 'a', events: ['NOT_AN_EVENT'] });
      api.ofKind('events')[0]!.stream.error(
        new ConcordatError({
          status: 400,
          code: 'subscription_invalid',
          detail: 'Unknown event.',
        }),
      );

      expect(store.rowErrors()['a']?.code).toBe('subscription_invalid');
      expect(store.subscriptions()[0]?.events).toEqual(['VERSION_PROMOTED']);
      expect(store.busy()).toEqual([]);
    });

    it('ignores a second change for a subscription already being changed', () => {
      loaded();

      store.setEvents({ id: 'a', events: ['VERSION_PROMOTED'] });
      store.setEvents({ id: 'a', events: [] });

      expect(api.ofKind('events')).toHaveLength(1);
    });

    it('lets two different subscriptions change at once', () => {
      loaded([subscription('a'), subscription('b')]);

      store.setEvents({ id: 'a', events: ['VERSION_PROMOTED'] });
      store.setEvents({ id: 'b', events: [] });

      expect(api.ofKind('events')).toHaveLength(2);
    });

    it('clears a row’s earlier refusal when it tries again', () => {
      loaded();
      store.setEvents({ id: 'a', events: ['X'] });
      api.ofKind('events')[0]!.stream.error(new Error('offline'));
      expect(store.rowErrors()['a']).toBeDefined();

      store.setEvents({ id: 'a', events: [] });

      expect(store.rowErrors()['a']).toBeUndefined();
    });
  });
  describe('loading', () => {
    it('shows subscriptions together with the outbox depth', () => {
      setUp(() => of({ pending: 3, parked: 2 }));
      loaded();

      expect(store.subscriptions()).toHaveLength(1);
      expect(store.outbox()).toEqual({ pending: 3, parked: 2 });
    });

    it('does not lose the subscriptions because the depth could not be read', () => {
      // The depth is a warning about the subscriptions, not the point of the screen.
      setUp(() => throwError(() => new Error('outbox down')));
      loaded();

      expect(store.subscriptions()).toHaveLength(1);
      expect(store.outbox()).toBeNull();
      expect(store.error()).toBeNull();
    });

    it('reports a failed list, and is not “empty”', () => {
      store.load();
      api
        .ofKind('list')[0]!
        .stream.error(
          new ConcordatError({ status: 404, code: 'environment_not_found', detail: 'no env' }),
        );

      expect(store.error()?.code).toBe('environment_not_found');
    });

    it('is empty only after a load that found nothing', () => {
      expect(store.isEmpty()).toBe(false);
      loaded([]);

      expect(store.isEmpty()).toBe(true);
    });
  });

  describe('creating', () => {
    const request: NewSubscription = {
      channel: 'WEBHOOK',
      endpoint: 'https://hooks.example.com/new',
      events: [],
    };

    it('holds a webhook’s signing secret, with the endpoint it belongs to, until dismissed', () => {
      store.create(request);
      api.ofKind('create')[0]!.stream.next({
        subscription: subscription('n', { endpoint: request.endpoint }),
        signingSecret: 'whsec_SECRET',
      });

      expect(store.signingSecret()).toEqual({ endpoint: request.endpoint, secret: 'whsec_SECRET' });
      expect(JSON.stringify(store.subscriptions())).not.toContain('SECRET');

      store.dismiss();

      expect(store.signingSecret()).toBeNull();
    });

    it('holds no secret for an email subscription', () => {
      store.create({ channel: 'EMAIL', endpoint: 'a@b.c', events: [] });
      api.ofKind('create')[0]!.stream.next({
        subscription: subscription('n', { channel: 'EMAIL', endpoint: 'a@b.c' }),
        signingSecret: null,
      });

      expect(store.signingSecret()).toBeNull();
      expect(store.created()?.channel).toBe('EMAIL');
    });

    it('adds the subscription from the registry’s answer', () => {
      loaded([subscription('a')]);
      store.create(request);
      api
        .ofKind('create')[0]!
        .stream.next({ subscription: subscription('n'), signingSecret: null });

      expect(store.subscriptions().map((s) => s.id)).toEqual(['a', 'n']);
    });

    it('clears the previous secret the moment a new create starts', () => {
      store.create(request);
      api.ofKind('create')[0]!.stream.next({
        subscription: subscription('n'),
        signingSecret: 'FIRST',
      });

      store.create(request);

      expect(store.signingSecret()).toBeNull();
      expect(store.created()).toBeNull();
    });

    it('ignores a second click while the first is in flight, which would be a second secret', () => {
      store.create(request);
      store.create(request);

      expect(api.ofKind('create')).toHaveLength(1);
    });

    it('keeps a refusal apart from the list', () => {
      loaded([subscription('a')]);
      store.create(request);
      api
        .ofKind('create')[0]!
        .stream.error(
          new ConcordatError({ status: 400, code: 'webhook_not_https', detail: 'https only' }),
        );

      expect(store.createError()?.detail).toBe('https only');
      expect(store.subscriptions()).toHaveLength(1);
      expect(store.error()).toBeNull();
    });
  });

  describe('muting and deleting', () => {
    beforeEach(() => loaded([subscription('a'), subscription('b')]));

    it('applies a mute from the registry’s answer, not ahead of it', () => {
      store.setEnabled({ id: 'a', enabled: false });

      expect(store.subscriptions()[0]?.enabled).toBe(true);
      expect(store.busy()).toEqual(['a']);

      api.ofKind('enabled')[0]!.stream.next(subscription('a', { enabled: false }));

      expect(store.subscriptions()[0]?.enabled).toBe(false);
      expect(store.busy()).toEqual([]);
    });

    it('removes a subscription only once the registry says it is gone', () => {
      store.remove('a');

      expect(store.subscriptions()).toHaveLength(2);

      api.ofKind('delete')[0]!.stream.next(undefined);

      expect(store.subscriptions().map((s) => s.id)).toEqual(['b']);
    });

    it('ignores a second change to a subscription already being changed', () => {
      store.setEnabled({ id: 'a', enabled: false });
      store.remove('a');

      expect(api.calls.filter((c) => c.kind !== 'list')).toHaveLength(1);
    });

    it('lets two different subscriptions change at once', () => {
      store.setEnabled({ id: 'a', enabled: false });
      store.setEnabled({ id: 'b', enabled: false });

      expect(api.ofKind('enabled')).toHaveLength(2);
    });

    it('keeps a refusal on the row it concerns, and leaves the subscription as it was', () => {
      store.remove('a');
      api
        .ofKind('delete')[0]!
        .stream.error(
          new ConcordatError({ status: 404, code: 'subscription_not_found', detail: 'gone' }),
        );

      expect(store.rowErrors()['a']?.detail).toBe('gone');
      expect(store.subscriptions()).toHaveLength(2);
      expect(store.busy()).toEqual([]);
    });

    it('clears a row’s refusal when it is tried again', () => {
      store.remove('a');
      api.ofKind('delete')[0]!.stream.error(new Error('offline'));
      store.remove('a');

      expect(store.rowErrors()['a']).toBeUndefined();
    });
  });
});

describe('OutboxDepth', () => {
  it('has the two counts a warning is made from', () => {
    const depth: OutboxDepth = { pending: 1, parked: 2 };

    expect(Object.keys(depth).sort()).toEqual(['parked', 'pending']);
  });
});
