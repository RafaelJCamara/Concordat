import { TestBed } from '@angular/core/testing';
import { Subject as ResponseStream, throwError } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { ConcordatError } from '../../../core/http/problem-details';
import type { Divergence } from '../../../domain/registry/compatibility';
import type { SchemaDocument } from '../../../domain/registry/schema';
import type { SchemaDiffRequest, SchemaDiffResult } from '../../../domain/registry/schema-diff';
import { SchemasApi } from '../data-access/schemas-api';
import { SchemaDiffFailure, SchemaDiffer } from '../data-access/schema-differ';
import { SchemaDiffStore } from './schema-diff-store';

// What matters is the sequence and the outcomes: both documents are fetched and the diff is asked
// for only when both are back, the registry's findings go to the worker numbered by position (the
// page finds them again by that number), and every way it can fail is a state the page can show.

const divergence = (path: string): Divergence => ({
  path,
  kind: 'type_narrowed',
  direction: 'BACKWARD',
  surface: 'WIRE_JSON',
  message: 'm',
  conflictsWithVersion: 1,
});

const document = (schemaId: string, text: string): SchemaDocument => ({
  schemaId,
  format: 'json',
  text,
  references: [],
});

const done: SchemaDiffResult = {
  status: 'ready',
  segments: [],
  lines: [],
  added: 0,
  removed: 0,
  unplaced: [],
};

function fakes() {
  const fetched: string[] = [];
  const streams = new Map<string, ResponseStream<SchemaDocument>>();
  const asked: SchemaDiffRequest[] = [];
  const answers: ResponseStream<SchemaDiffResult>[] = [];

  return {
    fetched,
    streams,
    asked,
    answers,
    schemas: {
      getSchema(id: string) {
        fetched.push(id);
        const stream = new ResponseStream<SchemaDocument>();
        streams.set(id, stream);
        return stream.asObservable();
      },
    },
    differ: {
      compare(request: SchemaDiffRequest) {
        asked.push(request);
        const stream = new ResponseStream<SchemaDiffResult>();
        answers.push(stream);
        return stream.asObservable();
      },
    },
  };
}

describe('SchemaDiffStore', () => {
  let fake: ReturnType<typeof fakes>;
  let store: InstanceType<typeof SchemaDiffStore>;

  const ref = { from: 'a', to: 'b', divergences: [divergence('#/x'), divergence('#/y')] };

  beforeEach(() => {
    fake = fakes();

    TestBed.configureTestingModule({
      providers: [
        SchemaDiffStore,
        { provide: SchemasApi, useValue: fake.schemas },
        { provide: SchemaDiffer, useValue: fake.differ },
      ],
    });
    store = TestBed.inject(SchemaDiffStore);
  });

  it('is idle until asked, so a review left on the summary fetches nothing', () => {
    expect(fake.fetched).toEqual([]);
    expect(store.loading()).toBe(false);
    expect(store.result()).toBeNull();
    expect(store.problem()).toBeNull();
  });

  it('fetches both schemas together, and compares only once both are back', () => {
    store.load(ref);

    expect(fake.fetched).toEqual(['a', 'b']);
    expect(store.loading()).toBe(true);

    fake.streams.get('a')?.next(document('a', '{"a":1}'));
    fake.streams.get('a')?.complete();

    expect(fake.asked).toEqual([]);

    fake.streams.get('b')?.next(document('b', '{"a":2}'));
    fake.streams.get('b')?.complete();

    expect(fake.asked).toHaveLength(1);
  });

  it('sends the baseline as before, the held version as after, and the findings numbered', () => {
    store.load(ref);
    fake.streams.get('a')?.next(document('a', 'BEFORE'));
    fake.streams.get('a')?.complete();
    fake.streams.get('b')?.next({ ...document('b', 'AFTER'), format: 'avro' });
    fake.streams.get('b')?.complete();

    expect(fake.asked[0]).toEqual({
      format: 'avro',
      before: 'BEFORE',
      after: 'AFTER',
      findings: [
        { id: 0, path: '#/x' },
        { id: 1, path: '#/y' },
      ],
    });
  });

  it('holds the result and stops loading when the comparison answers', () => {
    store.load(ref);
    fake.streams.get('a')?.next(document('a', '{}'));
    fake.streams.get('a')?.complete();
    fake.streams.get('b')?.next(document('b', '{}'));
    fake.streams.get('b')?.complete();
    fake.answers[0]?.next(done);

    expect(store.result()).toEqual(done);
    expect(store.loading()).toBe(false);
    expect(store.problem()).toBeNull();
  });

  it('holds a too-large answer as a result, not a problem', () => {
    const tooLarge: SchemaDiffResult = { status: 'too-large', reason: 'changes', limit: 1000 };
    store.load(ref);
    fake.streams.get('a')?.next(document('a', '{}'));
    fake.streams.get('a')?.complete();
    fake.streams.get('b')?.next(document('b', '{}'));
    fake.streams.get('b')?.complete();
    fake.answers[0]?.next(tooLarge);

    expect(store.result()).toEqual(tooLarge);
    expect(store.problem()).toBeNull();
  });

  it('reports a schema that could not be fetched, with the registry’s own detail', () => {
    store.load(ref);
    fake.streams
      .get('a')
      ?.error(
        new ConcordatError({ status: 404, code: 'schema_not_found', detail: 'No such schema.' }),
      );

    expect(store.problem()).toEqual({ reason: 'load', detail: 'No such schema.' });
    expect(store.loading()).toBe(false);
    expect(store.result()).toBeNull();
  });

  it.each(['timeout', 'unavailable'] as const)('reports a worker failure: %s', (reason) => {
    store.load(ref);
    fake.streams.get('a')?.next(document('a', '{}'));
    fake.streams.get('a')?.complete();
    fake.streams.get('b')?.next(document('b', '{}'));
    fake.streams.get('b')?.complete();
    fake.answers[0]?.error(new SchemaDiffFailure(reason, 'worker said so'));

    expect(store.problem()).toEqual({ reason, detail: 'worker said so' });
    expect(store.loading()).toBe(false);
  });

  it('forgets the last outcome when asked again', () => {
    store.load(ref);
    fake.streams.get('a')?.error(new ConcordatError({ status: 500, code: 'x', detail: 'down' }));

    expect(store.problem()).not.toBeNull();

    store.load({ ...ref, from: 'c', to: 'd' });

    expect(store.problem()).toBeNull();
    expect(store.loading()).toBe(true);
    expect(fake.fetched.slice(-2)).toEqual(['c', 'd']);
  });

  it('does not fail on a thrown error that is not the registry’s', () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        SchemaDiffStore,
        { provide: SchemasApi, useValue: { getSchema: () => throwError(() => new Error('boom')) } },
        { provide: SchemaDiffer, useValue: fake.differ },
      ],
    });
    const failing = TestBed.inject(SchemaDiffStore);

    failing.load(ref);

    expect(failing.problem()?.reason).toBe('load');
  });
});
