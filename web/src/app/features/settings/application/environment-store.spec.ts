import { TestBed } from '@angular/core/testing';
import { Subject as ResponseStream } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { Broker, Environment } from '../../../domain/environments/environment';
import { EnvironmentsApi } from '../data-access/environments-api';
import { EnvironmentStore } from './environment-store';

// What matters is that the environment on screen is only ever one the registry returned, that
// one write runs at a time, that a refusal is kept with the control that caused it — and that a
// broker password is never held.

function broker(id = 'b1', overrides: Partial<Broker> = {}): Broker {
  return {
    brokerId: id,
    displayName: `broker ${id}`,
    uri: 'amqp://x',
    virtualHost: '/',
    useTls: false,
    hasCredentials: false,
    status: 'UNKNOWN',
    lastCheckedAt: null,
    lastError: null,
    ...overrides,
  };
}

function environment(overrides: Partial<Environment> = {}): Environment {
  return {
    name: 'dev',
    description: null,
    defaultCompatibility: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
    registrationPolicy: 'OPEN',
    allowPreReleaseVersions: false,
    createdAt: new Date('2026-08-01T09:00:00Z'),
    brokers: [],
    ...overrides,
  };
}

function fakeApi() {
  const gets: ResponseStream<Environment>[] = [];
  const writes: { kind: string; args: unknown[]; stream: ResponseStream<Environment> }[] = [];

  function write(kind: string, ...args: unknown[]) {
    const stream = new ResponseStream<Environment>();
    writes.push({ kind, args, stream });
    return stream.asObservable();
  }

  return {
    gets,
    writes,
    getEnvironment() {
      const stream = new ResponseStream<Environment>();
      gets.push(stream);
      return stream.asObservable();
    },
    updateEnvironment: (...a: unknown[]) => write('update', ...a),
    addBroker: (...a: unknown[]) => write('add', ...a),
    checkBroker: (...a: unknown[]) => write('check', ...a),
    setBrokerCredential: (...a: unknown[]) => write('credential', ...a),
    removeBrokerCredential: (...a: unknown[]) => write('remove-credential', ...a),
    removeBroker: (...a: unknown[]) => write('remove', ...a),
  };
}

describe('EnvironmentStore', () => {
  let api: ReturnType<typeof fakeApi>;
  let store: InstanceType<typeof EnvironmentStore>;

  beforeEach(() => {
    api = fakeApi();

    TestBed.configureTestingModule({
      providers: [
        provideConcordatConfig({ defaultEnvironment: 'dev' }),
        { provide: EnvironmentsApi, useValue: api },
        EnvironmentStore,
      ],
    });

    store = TestBed.inject(EnvironmentStore);
  });

  function loaded(value: Environment = environment()): void {
    store.load();
    api.gets[0]!.next(value);
  }

  it('loads the active environment', () => {
    loaded();

    expect(store.environment()?.name).toBe('dev');
    expect(store.loading()).toBe(false);
  });

  it('reports a failure with no environment', () => {
    store.load();
    api.gets[0]!.error(
      new ConcordatError({ status: 404, code: 'environment_not_found', detail: 'no' }),
    );

    expect(store.environment()).toBeNull();
    expect(store.error()?.code).toBe('environment_not_found');
  });

  describe('settings', () => {
    it('sends only the changes it was given, for the active environment', () => {
      loaded();
      store.update({ description: 'new' });

      expect(api.writes[0]).toMatchObject({
        kind: 'update',
        args: ['dev', { description: 'new' }],
      });
    });

    it('replaces the environment with the one the registry returned', () => {
      loaded();
      store.update({ allowPreReleaseVersions: true });
      api.writes[0]!.stream.next(environment({ allowPreReleaseVersions: true }));

      expect(store.environment()?.allowPreReleaseVersions).toBe(true);
      expect(store.lastSaved()).toBe('settings');
      expect(store.saving()).toBeNull();
    });
  });

  describe('brokers', () => {
    it('adds one, and shows it only once the registry returned it', () => {
      loaded();
      store.addBroker({ displayName: 'p', uri: 'amqp://x', virtualHost: '' });

      expect(store.environment()?.brokers).toEqual([]);

      api.writes[0]!.stream.next(environment({ brokers: [broker('b1')] }));

      expect(store.environment()?.brokers).toHaveLength(1);
    });

    it('takes a check’s result from the returned environment, not from a guess', () => {
      loaded(environment({ brokers: [broker('b1')] }));
      store.checkBroker('b1');
      api.writes[0]!.stream.next(
        environment({
          brokers: [broker('b1', { status: 'UNREACHABLE', lastError: 'connection refused' })],
        }),
      );

      expect(store.environment()?.brokers[0]?.status).toBe('UNREACHABLE');
      expect(store.environment()?.brokers[0]?.lastError).toBe('connection refused');
    });

    it('keys each action by its broker, so one broker’s check is not another’s', () => {
      loaded(environment({ brokers: [broker('b1'), broker('b2')] }));
      store.checkBroker('b2');

      expect(store.saving()).toBe('check:b2');
    });

    it('sets a credential without keeping the password anywhere in its state', () => {
      loaded(environment({ brokers: [broker('b1')] }));
      store.setCredential({ brokerId: 'b1', username: 'guest', password: 'hunter2' });
      api.writes[0]!.stream.next(
        environment({ brokers: [broker('b1', { hasCredentials: true })] }),
      );

      expect(JSON.stringify(store.environment())).not.toContain('hunter2');
      expect(JSON.stringify(store.actionErrors())).not.toContain('hunter2');
      expect(store.environment()?.brokers[0]?.hasCredentials).toBe(true);
      expect(api.writes[0]!.args).toEqual([
        'dev',
        'b1',
        { username: 'guest', password: 'hunter2' },
      ]);
    });

    it('removes credentials and brokers by id', () => {
      loaded(environment({ brokers: [broker('b1', { hasCredentials: true })] }));
      store.removeCredential('b1');
      api.writes[0]!.stream.next(environment({ brokers: [broker('b1')] }));
      store.removeBroker('b1');

      expect(api.writes.map((w) => w.kind)).toEqual(['remove-credential', 'remove']);
    });
  });

  describe('one write at a time', () => {
    it('ignores a second change while the first is in flight', () => {
      loaded(environment({ brokers: [broker('b1')] }));
      store.checkBroker('b1');
      store.update({ description: 'x' });
      store.removeBroker('b1');

      expect(api.writes).toHaveLength(1);
    });

    it('accepts the next change once the first has settled', () => {
      loaded();
      store.update({ description: 'x' });
      api.writes[0]!.stream.next(environment({ description: 'x' }));
      store.update({ description: 'y' });

      expect(api.writes).toHaveLength(2);
    });

    it('does nothing before an environment has loaded', () => {
      store.update({ description: 'x' });

      expect(api.writes).toEqual([]);
    });
  });

  describe('a refusal', () => {
    it('is kept with the action that caused it, and the environment stays', () => {
      loaded();
      store.addBroker({ displayName: 'p', uri: 'amqp://x', virtualHost: '' });
      api.writes[0]!.stream.error(
        new ConcordatError({ status: 409, code: 'broker_exists', detail: 'name taken' }),
      );

      expect(store.actionErrors()['add-broker']?.detail).toBe('name taken');
      expect(store.error()).toBeNull();
      expect(store.environment()?.name).toBe('dev');
      expect(store.saving()).toBeNull();
      expect(store.lastSaved()).toBeNull();
    });

    it('is cleared when the same action is tried again', () => {
      loaded();
      store.update({ description: 'x' });
      api.writes[0]!.stream.error(new Error('offline'));

      store.update({ description: 'x' });

      expect(store.actionErrors()['settings']).toBeUndefined();
    });
  });
});
