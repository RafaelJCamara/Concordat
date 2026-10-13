import { TestBed } from '@angular/core/testing';
import { Subject as ResponseStream } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { CompatibilityPolicy } from '../../../domain/registry/compatibility';
import type { Subject } from '../../../domain/registry/subject';
import { SubjectsApi } from '../data-access/subjects-api';
import { SubjectDetailStore } from './subject-detail-store';
import { SubjectSettingsStore } from './subject-settings-store';

// Three things are load-bearing. What the screen reports is what the registry *answered* and not
// what was sent — it trims an owner, and a `PUT` answers with a policy and not a subject. A change
// that landed is followed by a fresh read of the subject, because neither answer is the page. And a
// second confirmation must not cancel the first: this store writes.

function subject(overrides: Partial<Subject> = {}): Subject {
  return {
    name: 'orders.created',
    format: 'json',
    owner: 'orders-team',
    lifecycle: 'ACTIVE',
    contentModel: 'OPEN',
    compatibilityPolicy: { mode: null, surface: null },
    latest: null,
    versions: [],
    ...overrides,
  };
}

type Call<T> = { args: unknown[]; stream: ResponseStream<T> };

function fakeApi() {
  const owners: Call<Subject>[] = [];
  const policies: Call<CompatibilityPolicy>[] = [];
  const reads: Call<Subject>[] = [];

  return {
    owners,
    policies,
    reads,
    changeOwner(...args: unknown[]) {
      const stream = new ResponseStream<Subject>();
      owners.push({ args, stream });
      return stream.asObservable();
    },
    setPolicy(...args: unknown[]) {
      const stream = new ResponseStream<CompatibilityPolicy>();
      policies.push({ args, stream });
      return stream.asObservable();
    },
    getSubject(...args: unknown[]) {
      const stream = new ResponseStream<Subject>();
      reads.push({ args, stream });
      return stream.asObservable();
    },
  };
}

function answer<T>(calls: Call<T>[], value: T): void {
  const { stream } = calls[calls.length - 1]!;
  stream.next(value);
  stream.complete();
}

const refusal = (code: string, status: number) =>
  new ConcordatError({ status, code, detail: `The registry said ${code}.` });

describe('SubjectSettingsStore', () => {
  let api: ReturnType<typeof fakeApi>;
  let store: InstanceType<typeof SubjectSettingsStore>;
  let detail: InstanceType<typeof SubjectDetailStore>;

  beforeEach(() => {
    api = fakeApi();
    TestBed.configureTestingModule({
      providers: [
        provideConcordatConfig({ defaultEnvironment: 'dev' }),
        { provide: SubjectsApi, useValue: api },
        SubjectDetailStore,
        SubjectSettingsStore,
      ],
    });

    detail = TestBed.inject(SubjectDetailStore);
    store = TestBed.inject(SubjectSettingsStore);

    // The page has read the subject before it can be edited.
    detail.load('orders.created');
    answer(api.reads, subject());
    api.reads.length = 0;
  });

  it('starts idle, with nothing changed and nothing refused', () => {
    expect(store.ownerSaving()).toBe(false);
    expect(store.policySaving()).toBe(false);
    expect(store.ownerError()).toBeNull();
    expect(store.policyError()).toBeNull();
    expect(store.outcome()).toBeNull();
  });

  describe('changing the owner', () => {
    it('asks the active environment, for that subject, with exactly the owner it was given', () => {
      store.changeOwner({ subject: 'orders.created', owner: 'new-team' });

      expect(api.owners).toHaveLength(1);
      expect(api.owners[0]?.args).toEqual(['dev', 'orders.created', 'new-team']);
      expect(store.ownerSaving()).toBe(true);
    });

    it('reports the owner the registry answered with, not the one it was sent', () => {
      // The registry trims. What is said to have happened is what it says happened.
      store.changeOwner({ subject: 'orders.created', owner: '  new-team  ' });
      answer(api.owners, subject({ owner: 'new-team' }));

      expect(store.ownerSaving()).toBe(false);
      expect(store.outcome()).toEqual({
        kind: 'owner',
        subject: 'orders.created',
        owner: 'new-team',
      });
    });

    it('reads the subject again once it has landed, and the page shows that and not a guess', () => {
      store.changeOwner({ subject: 'orders.created', owner: 'new-team' });
      expect(api.reads).toHaveLength(0);

      answer(api.owners, subject({ owner: 'new-team' }));

      expect(api.reads).toHaveLength(1);
      expect(api.reads[0]?.args).toEqual(['dev', 'orders.created']);
      // Until the read answers, the page still shows what it last read.
      expect(detail.subject()?.owner).toBe('orders-team');

      answer(api.reads, subject({ owner: 'what-the-registry-now-says' }));

      expect(detail.subject()?.owner).toBe('what-the-registry-now-says');
    });

    it('does not read again when the registry refused', () => {
      store.changeOwner({ subject: 'orders.created', owner: 'new-team' });
      api.owners[0]!.stream.error(refusal('lifecycle_transition_invalid', 409));

      expect(api.reads).toHaveLength(0);
    });

    it('keeps a refusal as state with the registry’s code, and is not a changed owner', () => {
      store.changeOwner({ subject: 'orders.created', owner: 'new-team' });
      api.owners[0]!.stream.error(refusal('lifecycle_transition_invalid', 409));

      expect(store.ownerSaving()).toBe(false);
      expect(store.outcome()).toBeNull();
      expect(store.ownerError()?.code).toBe('lifecycle_transition_invalid');
      expect(store.ownerError()?.status).toBe(409);
    });

    it('can try again after a refusal', () => {
      // A failure that propagated would tear the rxMethod down and the next attempt would do
      // nothing at all, which reads as a button that stopped working.
      store.changeOwner({ subject: 'orders.created', owner: 'a' });
      api.owners[0]!.stream.error(refusal('actor_id_invalid', 400));
      store.changeOwner({ subject: 'orders.created', owner: 'b' });

      expect(api.owners).toHaveLength(2);
      expect(store.ownerError()).toBeNull();
      expect(store.ownerSaving()).toBe(true);
    });

    it('ignores a second confirmation while one is in flight, and does not cancel the first', () => {
      store.changeOwner({ subject: 'orders.created', owner: 'a' });
      store.changeOwner({ subject: 'orders.created', owner: 'b' });

      expect(api.owners).toHaveLength(1);
      expect(api.owners[0]?.stream.closed).toBe(false);
    });
  });

  describe('setting the policy', () => {
    const whole: CompatibilityPolicy = { mode: 'FULL', surface: 'SOURCE' };

    it('asks the active environment, for that subject, with exactly the policy it was given', () => {
      store.setPolicy({ subject: 'orders.created', policy: whole });

      expect(api.policies[0]?.args).toEqual(['dev', 'orders.created', whole]);
      expect(store.policySaving()).toBe(true);
    });

    it('reports the policy the registry answered with, since the answer is not a subject', () => {
      store.setPolicy({ subject: 'orders.created', policy: whole });
      answer(api.policies, { mode: 'FULL_TRANSITIVE', surface: 'SOURCE' });

      expect(store.outcome()).toEqual({
        kind: 'policy',
        subject: 'orders.created',
        policy: { mode: 'FULL_TRANSITIVE', surface: 'SOURCE' },
      });
    });

    it('reads the subject again, so the page is not left showing the old policy', () => {
      store.setPolicy({ subject: 'orders.created', policy: whole });
      answer(api.policies, whole);

      expect(api.reads).toHaveLength(1);
      expect(detail.subject()?.compatibilityPolicy).toEqual({ mode: null, surface: null });

      answer(api.reads, subject({ compatibilityPolicy: whole }));

      expect(detail.subject()?.compatibilityPolicy).toEqual(whole);
    });

    it('clears with two nulls, and reports inheriting when the registry says so', () => {
      store.setPolicy({ subject: 'orders.created', policy: { mode: null, surface: null } });
      answer(api.policies, { mode: null, surface: null });

      expect(store.outcome()).toMatchObject({ kind: 'policy', policy: { mode: null } });
    });

    it('keeps a refusal as state, apart from the owner’s', () => {
      store.setPolicy({ subject: 'orders.created', policy: whole });
      api.policies[0]!.stream.error(refusal('insufficient_scope', 403));

      expect(store.policyError()?.code).toBe('insufficient_scope');
      expect(store.ownerError()).toBeNull();
      expect(store.outcome()).toBeNull();
      expect(api.reads).toHaveLength(0);
    });

    it('ignores a second confirmation while one is in flight', () => {
      store.setPolicy({ subject: 'orders.created', policy: whole });
      store.setPolicy({ subject: 'orders.created', policy: { mode: null, surface: null } });

      expect(api.policies).toHaveLength(1);
    });
  });

  it('does not make one write wait for the other', () => {
    store.changeOwner({ subject: 'orders.created', owner: 'a' });
    store.setPolicy({ subject: 'orders.created', policy: { mode: 'FULL', surface: 'WIRE' } });

    expect(api.owners).toHaveLength(1);
    expect(api.policies).toHaveLength(1);
  });

  it('drops the previous answer when a new attempt starts, and when told to reset', () => {
    store.changeOwner({ subject: 'orders.created', owner: 'a' });
    answer(api.owners, subject({ owner: 'a' }));
    expect(store.outcome()).not.toBeNull();

    store.setPolicy({ subject: 'orders.created', policy: { mode: 'FULL', surface: 'WIRE' } });
    expect(store.outcome()).toBeNull();

    api.policies[0]!.stream.error(refusal('insufficient_scope', 403));
    store.reset();

    expect(store.policyError()).toBeNull();
  });
});
