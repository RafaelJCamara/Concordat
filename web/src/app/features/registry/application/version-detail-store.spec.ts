import { TestBed } from '@angular/core/testing';
import { Subject as ResponseStream } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { SchemaDocument } from '../../../domain/registry/schema';
import type { SchemaVersion, Subject } from '../../../domain/registry/subject';
import { SchemasApi } from '../data-access/schemas-api';
import { SubjectsApi } from '../data-access/subjects-api';
import { VersionDetailStore } from './version-detail-store';

// Two things here are worth more than the rest of the file. `latest` has to resolve through
// the subject's gated pointer rather than by taking the highest ordinal (ADR-017) — the whole
// point of the gate is that the newest version is sometimes deliberately not the current one.
// And the two requests are sequential, so a failure of the second must not discard what the
// first already proved.

function version(ordinal: number, status: SchemaVersion['status'] = 'ACTIVE'): SchemaVersion {
  return {
    ordinal,
    schemaId: `schema-${ordinal}`,
    semanticVersion: `1.${ordinal}.0`,
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

const document: SchemaDocument = {
  schemaId: 'schema-1',
  format: 'json',
  text: '{"type":"object"}',
  references: [],
};

function fakeSubjects() {
  const streams: ResponseStream<Subject>[] = [];
  const asked: { environment: string; name: string }[] = [];

  return {
    asked,
    streams,
    latest: () => streams[streams.length - 1]!,
    getSubject(environment: string, name: string) {
      asked.push({ environment, name });
      const stream = new ResponseStream<Subject>();
      streams.push(stream);
      return stream.asObservable();
    },
  };
}

function fakeSchemas() {
  const streams: ResponseStream<SchemaDocument>[] = [];
  const asked: string[] = [];

  return {
    asked,
    streams,
    latest: () => streams[streams.length - 1]!,
    getSchema(schemaId: string) {
      asked.push(schemaId);
      const stream = new ResponseStream<SchemaDocument>();
      streams.push(stream);
      return stream.asObservable();
    },
  };
}

describe('VersionDetailStore', () => {
  let subjects: ReturnType<typeof fakeSubjects>;
  let schemas: ReturnType<typeof fakeSchemas>;
  let store: InstanceType<typeof VersionDetailStore>;

  beforeEach(() => {
    subjects = fakeSubjects();
    schemas = fakeSchemas();

    TestBed.configureTestingModule({
      providers: [
        provideConcordatConfig({ defaultEnvironment: 'dev' }),
        { provide: SubjectsApi, useValue: subjects },
        { provide: SchemasApi, useValue: schemas },
        VersionDetailStore,
      ],
    });

    store = TestBed.inject(VersionDetailStore);
  });

  it('starts idle rather than loading', () => {
    expect(store.loading()).toBe(false);
    expect(store.loadingDocument()).toBe(false);
    expect(store.version()).toBeNull();
    expect(store.document()).toBeNull();
    expect(store.error()).toBeNull();
  });

  it('publishes the metadata before the document has arrived', () => {
    // Why there are two loading flags. Holding the header back until the schema lands would
    // keep the whole screen under a skeleton for the slower of two requests, when who
    // registered this and when is already known and is most of what a reader came for.
    store.load({ subject: 'orders.created', ordinal: '1' });
    subjects.latest().next(subject([version(1)], 1));

    expect(store.version()?.ordinal).toBe(1);
    expect(store.loading()).toBe(false);
    expect(store.loadingDocument()).toBe(true);
    expect(store.document()).toBeNull();
  });

  describe('resolving the ordinal', () => {
    it('finds a version by its number', () => {
      store.load({ subject: 'orders.created', ordinal: '2' });
      subjects.latest().next(subject([version(1), version(2)], 2));

      expect(store.version()?.ordinal).toBe(2);
    });

    it('resolves `latest` through the gated pointer, not the highest ordinal', () => {
      // The defect this exists to prevent. v3 is awaiting approval, so the registry's
      // pointer still says v2 — and a screen that took `max(ordinal)` would show an
      // unapproved schema as the current contract, which is exactly what the gate is for.
      store.load({ subject: 'orders.created', ordinal: 'latest' });
      subjects.latest().next(subject([version(1), version(2), version(3, 'AWAITING_APPROVAL')], 2));

      expect(store.version()?.ordinal).toBe(2);
    });

    it('accepts `latest` in any case, as the API does', () => {
      store.load({ subject: 'orders.created', ordinal: 'LATEST' });
      subjects.latest().next(subject([version(1)], 1));

      expect(store.version()?.ordinal).toBe(1);
    });

    it('refuses `latest` on a subject with no active version', () => {
      // A subject exists but nothing has been approved onto it. That is not an error state
      // for the subject, but there is genuinely no version to show.
      store.load({ subject: 'orders.created', ordinal: 'latest' });
      subjects.latest().next(subject([version(1, 'AWAITING_APPROVAL')], null));

      expect(store.version()).toBeNull();
      expect(store.error()?.code).toBe('version_not_found');
    });

    it('refuses an ordinal that does not exist, with the code the server would have used', () => {
      // Locally decided, but shaped like the server's refusal so the template renders it
      // through one path and a caller branching on the code needs no second case.
      store.load({ subject: 'orders.created', ordinal: '9' });
      subjects.latest().next(subject([version(1)], 1));

      expect(store.error()).toBeInstanceOf(ConcordatError);
      expect(store.error()?.code).toBe('version_not_found');
      expect(store.error()?.status).toBe(404);
      expect(store.loading()).toBe(false);
    });

    it('refuses a non-numeric ordinal without asking for a schema', () => {
      store.load({ subject: 'orders.created', ordinal: 'not-a-number' });
      subjects.latest().next(subject([version(1)], 1));

      expect(store.error()?.code).toBe('version_not_found');
      expect(schemas.asked).toEqual([]);
    });

    it('keeps the subject when the ordinal is wrong', () => {
      // The subject request succeeded. Discarding its answer would cost the screen the
      // context it needs to say which subject the missing version was missing from.
      store.load({ subject: 'orders.created', ordinal: '9' });
      subjects.latest().next(subject([version(1)], 1));

      expect(store.subject()?.name).toBe('orders.created');
    });
  });

  describe('fetching the document', () => {
    it('asks for the schema the resolved version points at', () => {
      // A version carries a schema id, never the text — schemas are deduplicated by content
      // and shared, so the document is a second request by design and not an oversight.
      store.load({ subject: 'orders.created', ordinal: '2' });
      subjects.latest().next(subject([version(1), version(2)], 2));

      expect(schemas.asked).toEqual(['schema-2']);
    });

    it('publishes both halves and stops loading', () => {
      store.load({ subject: 'orders.created', ordinal: '1' });
      subjects.latest().next(subject([version(1)], 1));
      schemas.latest().next(document);

      expect(store.version()?.ordinal).toBe(1);
      expect(store.document()?.text).toBe('{"type":"object"}');
      expect(store.loading()).toBe(false);
      expect(store.loadingDocument()).toBe(false);
      expect(store.error()).toBeNull();
    });

    it('keeps the metadata when only the document fails', () => {
      // A partial failure. Blanking the screen would throw away who registered what and
      // when, all of which arrived intact — and which is most of what a reader came for.
      store.load({ subject: 'orders.created', ordinal: '1' });
      subjects.latest().next(subject([version(1)], 1));
      schemas.latest().error(new Error('offline'));

      expect(store.version()?.ordinal).toBe(1);
      expect(store.document()).toBeNull();
      expect(store.error()).toBeInstanceOf(ConcordatError);
      expect(store.loading()).toBe(false);
      expect(store.loadingDocument()).toBe(false);
    });

    it('can still load after a failure', () => {
      // `catchError` sits inside the `switchMap` for this reason: outside it, the failure
      // tears down the rxMethod's subscription and every later load does nothing at all.
      store.load({ subject: 'orders.created', ordinal: '1' });
      subjects.latest().error(new Error('offline'));

      store.load({ subject: 'orders.created', ordinal: '1' });
      subjects.latest().next(subject([version(1)], 1));
      schemas.latest().next(document);

      expect(store.document()).not.toBeNull();
      expect(store.error()).toBeNull();
    });
  });

  describe('previous', () => {
    it('is the version one ordinal below', () => {
      store.load({ subject: 'orders.created', ordinal: '3' });
      subjects.latest().next(subject([version(1), version(2), version(3)], 3));

      expect(store.previous()?.ordinal).toBe(2);
    });

    it('is null on the first version, so no comparison is offered', () => {
      store.load({ subject: 'orders.created', ordinal: '1' });
      subjects.latest().next(subject([version(1)], 1));

      expect(store.previous()).toBeNull();
    });

    it('is null when the preceding ordinal is absent rather than picking the next one down', () => {
      // Ordinals are contiguous in practice, so this is defensive — but "previous" meaning
      // "whatever sorts before it" would silently compare against a version two changes
      // back and label the diff as one step.
      store.load({ subject: 'orders.created', ordinal: '3' });
      subjects.latest().next(subject([version(1), version(3)], 3));

      expect(store.previous()).toBeNull();
    });
  });

  describe('isLatest', () => {
    it('is true when the pointer resolves to this version', () => {
      store.load({ subject: 'orders.created', ordinal: '2' });
      subjects.latest().next(subject([version(1), version(2)], 2));

      expect(store.isLatest()).toBe(true);
    });

    it('is false for a higher ordinal the gate has not released', () => {
      store.load({ subject: 'orders.created', ordinal: '3' });
      subjects.latest().next(subject([version(2), version(3, 'AWAITING_APPROVAL')], 2));

      expect(store.isLatest()).toBe(false);
    });
  });

  it('discards a slow response that a newer request has superseded', () => {
    // Navigating between two versions quickly must not leave the first one's schema on
    // screen under the second one's heading.
    store.load({ subject: 'orders.created', ordinal: '1' });
    const slow = subjects.streams[0]!;

    store.load({ subject: 'orders.created', ordinal: '2' });
    const fast = subjects.streams[1]!;

    fast.next(subject([version(1), version(2)], 2));
    slow.next(subject([version(1), version(2)], 2));

    expect(store.version()?.ordinal).toBe(2);
  });
});
