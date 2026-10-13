import { TestBed } from '@angular/core/testing';
import { Subject as Stream } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import type { SchemaCheckRequest, SchemaCheckResult } from '../../../domain/registry/schema-check';
import { SchemaCheckFailure, SchemaChecker } from '../data-access/schema-checker';
import { SchemaCheckStore } from './schema-check-store';

// A check that could not run says nothing about the schema, and the store must not let it read
// as an answer. And a second ask supersedes the first, which may be the one that is hung.

const result = (checked: string): SchemaCheckResult => ({
  checked,
  skipped: null,
  advice: [],
  payload: null,
});

describe('SchemaCheckStore', () => {
  let asked: { request: SchemaCheckRequest; stream: Stream<SchemaCheckResult> }[];
  let store: InstanceType<typeof SchemaCheckStore>;

  beforeEach(() => {
    asked = [];

    TestBed.configureTestingModule({
      providers: [
        {
          provide: SchemaChecker,
          useValue: {
            check(request: SchemaCheckRequest) {
              const stream = new Stream<SchemaCheckResult>();
              asked.push({ request, stream });
              return stream.asObservable();
            },
          },
        },
        SchemaCheckStore,
      ],
    });

    store = TestBed.inject(SchemaCheckStore);
  });

  const request = (schema: string): SchemaCheckRequest => ({ schema, payload: null });

  it('starts with nothing to say', () => {
    expect(store.checking()).toBe(false);
    expect(store.result()).toBeNull();
    expect(store.failure()).toBeNull();
  });

  it('is checking, and remembers what it was asked, from the moment of the ask', () => {
    store.run(request('{"a":1}'));

    expect(store.checking()).toBe(true);
    expect(store.request()).toEqual(request('{"a":1}'));
  });

  it('holds the answer', () => {
    store.run(request('{}'));
    asked[0]!.stream.next(result('{}'));

    expect(store.result()?.checked).toBe('{}');
    expect(store.checking()).toBe(false);
  });

  it('clears the previous answer when a new ask begins', () => {
    store.run(request('{}'));
    asked[0]!.stream.next(result('{}'));

    store.run(request('{"b":2}'));

    expect(store.result()).toBeNull();
    expect(store.checking()).toBe(true);
  });

  it('records a failure as a failure, never as a result', () => {
    store.run(request('{}'));
    asked[0]!.stream.error(new SchemaCheckFailure('timeout', 'The check took too long.'));

    expect(store.failure()).toBe('The check took too long.');
    expect(store.result()).toBeNull();
    expect(store.checking()).toBe(false);
  });

  it('gives a plain message for a failure it does not recognise', () => {
    store.run(request('{}'));
    asked[0]!.stream.error(new Error('boom'));

    expect(store.failure()).toBe('The check could not be run.');
  });

  it('lets a later ask supersede one still in flight', () => {
    // The one abandoned may be the one hung on an expensive pattern.
    store.run(request('first'));
    store.run(request('second'));
    asked[0]!.stream.next(result('first'));
    asked[1]!.stream.next(result('second'));

    expect(store.result()?.checked).toBe('second');
  });

  it('can be cleared', () => {
    store.run(request('{}'));
    asked[0]!.stream.next(result('{}'));
    store.clear();

    expect(store.result()).toBeNull();
    expect(store.request()).toBeNull();
  });
});
