import { TestBed } from '@angular/core/testing';
import { Subject as ResponseStream } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { NewSubject } from '../../../domain/registry/new-subject';
import type { Subject } from '../../../domain/registry/subject';
import { SubjectsApi } from '../data-access/subjects-api';
import { NewSubjectStore } from './new-subject-store';

// Two things are load-bearing. What the registry answered is the only thing that says the subject
// exists — and its name, not the one sent, is what can be opened, because the registry trims. And
// a second submit must not cancel the first: this store writes, and `switchMap` would abandon the
// browser's half of a request the registry is still going to process.

const request: NewSubject = {
  name: 'orders.created',
  format: 'json',
  owner: 'orders-team',
  contentModel: 'OPEN',
  compatibility: null,
};

function stored(name = 'orders.created'): Subject {
  return {
    name,
    format: 'json',
    owner: 'orders-team',
    lifecycle: 'ACTIVE',
    contentModel: 'OPEN',
    compatibilityPolicy: { mode: null, surface: null },
    latest: null,
    versions: [],
  };
}

function fakeApi() {
  const calls: { environment: string; subject: NewSubject; stream: ResponseStream<Subject> }[] = [];

  return {
    calls,
    createSubject(environment: string, subject: NewSubject) {
      const stream = new ResponseStream<Subject>();
      calls.push({ environment, subject, stream });
      return stream.asObservable();
    },
    /** Answers the latest call the way `HttpClient` does: one value, then complete. */
    answer(value: Subject) {
      const { stream } = calls[calls.length - 1]!;
      stream.next(value);
      stream.complete();
    },
    refuse(error: unknown) {
      calls[calls.length - 1]!.stream.error(error);
    },
  };
}

const conflict = () =>
  new ConcordatError({
    status: 409,
    code: 'subject_already_exists',
    detail: "Subject 'orders.created' already exists in this environment.",
  });

describe('NewSubjectStore', () => {
  let api: ReturnType<typeof fakeApi>;
  let store: InstanceType<typeof NewSubjectStore>;

  beforeEach(() => {
    api = fakeApi();
    TestBed.configureTestingModule({
      providers: [
        provideConcordatConfig({ defaultEnvironment: 'dev' }),
        { provide: SubjectsApi, useValue: api },
        NewSubjectStore,
      ],
    });

    store = TestBed.inject(NewSubjectStore);
  });

  it('starts idle, with nothing created and nothing refused', () => {
    expect(store.submitting()).toBe(false);
    expect(store.created()).toBeNull();
    expect(store.error()).toBeNull();
  });

  it('creates in the active environment, with exactly what it was given', () => {
    store.create(request);

    expect(api.calls).toHaveLength(1);
    expect(api.calls[0]).toMatchObject({ environment: 'dev', subject: request });
    expect(store.submitting()).toBe(true);
  });

  it('holds the registry’s answer, and not the request, as the subject that exists', () => {
    store.create({ ...request, name: 'orders.created' });
    api.answer(stored('orders.created'));

    expect(store.submitting()).toBe(false);
    expect(store.created()?.name).toBe('orders.created');
  });

  it('takes the name from the answer when the registry stored a different one', () => {
    // It trims what it is sent before judging it. The subject that can be opened is the stored
    // one, so that is what a caller navigates to.
    store.create({ ...request, name: 'orders.created' });
    api.answer(stored('stored.by.the.registry'));

    expect(store.created()?.name).toBe('stored.by.the.registry');
  });

  it('is not created until the registry has said so', () => {
    store.create(request);

    expect(store.created()).toBeNull();
  });

  describe('a refusal', () => {
    it('is state, not a thrown error, and carries the registry’s code', () => {
      store.create(request);
      api.refuse(conflict());

      expect(store.submitting()).toBe(false);
      expect(store.created()).toBeNull();
      expect(store.error()?.error.code).toBe('subject_already_exists');
      expect(store.error()?.error.status).toBe(409);
    });

    it('remembers the name that was refused, so the way out can be offered', () => {
      store.create(request);
      api.refuse(conflict());

      expect(store.error()?.attempted).toBe('orders.created');
    });

    it('does not stop the store from trying again', () => {
      // A failure that propagated would tear the rxMethod down, and the next submit would do
      // nothing at all — which reads as a button that stopped working.
      store.create(request);
      api.refuse(conflict());
      store.create({ ...request, name: 'orders.shipped' });

      expect(api.calls).toHaveLength(2);
      expect(store.error()).toBeNull();
      expect(store.submitting()).toBe(true);
    });

    it('clears when told to', () => {
      store.create(request);
      api.refuse(conflict());
      store.reset();

      expect(store.error()).toBeNull();
    });
  });

  describe('a second submit while one is in flight', () => {
    it('is ignored, not sent and not allowed to cancel the first', () => {
      store.create(request);
      store.create({ ...request, name: 'orders.shipped' });

      expect(api.calls).toHaveLength(1);
      expect(api.calls[0]?.stream.closed).toBe(false);
    });

    it('is accepted once the first has answered', () => {
      store.create(request);
      api.answer(stored());
      store.create({ ...request, name: 'orders.shipped' });

      expect(api.calls).toHaveLength(2);
    });
  });

  it('drops a previous answer when a new attempt starts', () => {
    store.create(request);
    api.answer(stored());
    store.create({ ...request, name: 'orders.shipped' });

    expect(store.created()).toBeNull();
  });
});
