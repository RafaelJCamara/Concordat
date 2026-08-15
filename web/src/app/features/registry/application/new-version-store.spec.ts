import { TestBed } from '@angular/core/testing';
import { Subject as ResponseStream } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { RegistrationOutcome } from '../../../domain/registry/registration';
import type { SchemaVersion, Subject } from '../../../domain/registry/subject';
import { SubjectsApi } from '../data-access/subjects-api';
import { NewVersionStore } from './new-version-store';

// Two things here are load-bearing. A breaking change is a *success* that lands in `outcome`
// and never in `error` — reporting it as a failure would have people re-submitting a change
// the registry already accepted. And a second submit must not cancel the first: this is the
// one store in the app that writes, and `switchMap` would abandon the browser's half of a
// request the registry is still going to process.

function version(ordinal: number, status: SchemaVersion['status'] = 'ACTIVE'): SchemaVersion {
  return {
    ordinal,
    schemaId: `schema-${ordinal}`,
    semanticVersion: null,
    status,
    changelog: null,
    registeredAt: new Date('2026-08-13T09:00:00Z'),
    registeredBy: 'ci',
    deprecated: false,
  };
}

function subject(versions: readonly SchemaVersion[], latest: number | null): Subject {
  return {
    name: 'orders.created',
    format: 'json',
    owner: 'orders-team',
    lifecycle: 'ACTIVE',
    contentModel: 'OPEN',
    compatibilityPolicy: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
    latest,
    versions,
  };
}

function outcome(overrides: Partial<RegistrationOutcome> = {}): RegistrationOutcome {
  return {
    subject: 'orders.created',
    ordinal: 2,
    schemaId: 'schema-2',
    status: 'ACTIVE',
    created: true,
    divergences: [],
    portability: [],
    ...overrides,
  };
}

function fakeApi() {
  const registrations: ResponseStream<RegistrationOutcome>[] = [];
  const subjects: ResponseStream<Subject>[] = [];
  const sent: unknown[] = [];

  return {
    registrations,
    subjects,
    sent,
    latestRegistration: () => registrations[registrations.length - 1]!,
    latestSubject: () => subjects[subjects.length - 1]!,
    /**
     * Answers the in-flight registration the way `HttpClient` does: one value, then
     * complete.
     *
     * The completion is not a detail. `exhaustMap` ignores new submits until the *inner*
     * observable finishes, so a fake that only emitted would leave the store permanently
     * refusing the next submit — and the test for "accepts a submit once the first has
     * answered" would fail against a store that is in fact correct.
     */
    respond(value: RegistrationOutcome) {
      const stream = registrations[registrations.length - 1]!;
      stream.next(value);
      stream.complete();
    },
    /** Fails the in-flight registration, then completes, as an errored request does. */
    refuse(error: unknown) {
      registrations[registrations.length - 1]!.error(error);
    },
    getSubject() {
      const stream = new ResponseStream<Subject>();
      subjects.push(stream);
      return stream.asObservable();
    },
    registerVersion(environment: string, name: string, request: unknown) {
      sent.push({ environment, name, request });
      const stream = new ResponseStream<RegistrationOutcome>();
      registrations.push(stream);
      return stream.asObservable();
    },
  };
}

describe('NewVersionStore', () => {
  let api: ReturnType<typeof fakeApi>;
  let store: InstanceType<typeof NewVersionStore>;

  beforeEach(() => {
    api = fakeApi();
    TestBed.configureTestingModule({
      providers: [
        provideConcordatConfig({ defaultEnvironment: 'dev' }),
        { provide: SubjectsApi, useValue: api },
        NewVersionStore,
      ],
    });

    store = TestBed.inject(NewVersionStore);
  });

  const submit = () =>
    store.submit({
      subject: 'orders.created',
      schema: '{"type":"object"}',
      semanticVersion: null,
      changelog: null,
    });

  it('starts idle', () => {
    expect(store.submitting()).toBe(false);
    expect(store.outcome()).toBeNull();
    expect(store.error()).toBeNull();
  });

  describe('nextOrdinal', () => {
    it('clears the highest ordinal present, not the latest pointer', () => {
      // v3 is awaiting approval and has already taken its ordinal, so the next registration
      // is v4. Deriving this from `latest` would propose an ordinal that is already used.
      store.loadSubject('orders.created');
      api
        .latestSubject()
        .next(subject([version(1), version(2), version(3, 'AWAITING_APPROVAL')], 2));

      expect(store.nextOrdinal()).toBe(4);
    });

    it('is 1 on a subject with no versions', () => {
      store.loadSubject('orders.created');
      api.latestSubject().next(subject([], null));

      expect(store.nextOrdinal()).toBe(1);
    });

    it('is null before the subject has loaded', () => {
      expect(store.nextOrdinal()).toBeNull();
    });
  });

  describe('submitting', () => {
    it('sends the schema to the named subject in the active environment', () => {
      submit();

      expect(api.sent).toEqual([
        {
          environment: 'dev',
          name: 'orders.created',
          request: { schema: '{"type":"object"}', semanticVersion: null, changelog: null },
        },
      ]);
    });

    it('records a plain registration as a success', () => {
      submit();
      api.respond(outcome());

      expect(store.outcome()?.ordinal).toBe(2);
      expect(store.held()).toBe(false);
      expect(store.error()).toBeNull();
      expect(store.submitting()).toBe(false);
    });

    it('records a breaking change as a success that is held, not as an error', () => {
      // The registry accepted it and left `latest` unmoved (ADR-017). Putting this in
      // `error` would tell someone their change failed when it is sitting in a queue.
      submit();
      api.respond(
        outcome({
          status: 'AWAITING_APPROVAL',
          divergences: [
            {
              path: '/properties/discount',
              kind: 'required_field_added',
              direction: 'BACKWARD',
              surface: 'WIRE_JSON',
              message: 'A required field was added.',
              conflictsWithVersion: 1,
            },
          ],
        }),
      );

      expect(store.error()).toBeNull();
      expect(store.held()).toBe(true);
      expect(store.outcome()?.divergences).toHaveLength(1);
    });

    it('carries the unchanged case through, so the screen can say nothing happened', () => {
      // 200 rather than 201: re-registering the tip is idempotent and allocates no ordinal.
      // A client that treats the two alike double-counts versions.
      submit();
      api.respond(outcome({ created: false, ordinal: 1 }));

      expect(store.outcome()?.created).toBe(false);
    });

    it('captures a refusal as state rather than throwing', () => {
      submit();
      api.refuse(
        new ConcordatError({
          status: 400,
          code: 'schema_invalid',
          detail: 'Not a valid JSON Schema.',
        }),
      );

      expect(store.error()?.detail).toBe('Not a valid JSON Schema.');
      expect(store.outcome()).toBeNull();
      expect(store.submitting()).toBe(false);
    });

    it('ignores a second submit while the first is in flight', () => {
      // Why `exhaustMap` and not `switchMap`. A double-clicked button must not abandon the
      // browser's half of a POST the registry is still going to process — which, on this
      // endpoint, could allocate two ordinals while the screen showed one.
      submit();
      submit();

      expect(api.registrations).toHaveLength(1);
    });

    it('accepts a submit once the first has answered', () => {
      submit();
      api.respond(outcome());

      store.reset();
      submit();

      expect(api.registrations).toHaveLength(2);
    });

    it('can still submit after a failure', () => {
      submit();
      api.refuse(new Error('offline'));

      submit();
      api.respond(outcome());

      expect(store.outcome()).not.toBeNull();
      expect(store.error()).toBeNull();
    });

    it('clears the previous answer when a new attempt starts', () => {
      submit();
      api.respond(outcome());

      store.reset();
      submit();

      expect(store.outcome()).toBeNull();
      expect(store.submitting()).toBe(true);
    });
  });

  describe('reset', () => {
    it('clears the outcome so the form comes back', () => {
      submit();
      api.respond(outcome());

      store.reset();

      expect(store.outcome()).toBeNull();
      expect(store.error()).toBeNull();
    });
  });
});
