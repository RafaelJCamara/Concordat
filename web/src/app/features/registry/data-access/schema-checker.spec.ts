import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { SchemaCheckResult } from '../../../domain/registry/schema-check';
import {
  SCHEMA_CHECK_TIMEOUT_MS,
  SCHEMA_CHECK_WORKER,
  SchemaCheckFailure,
  SchemaChecker,
} from './schema-checker';

// There is no Worker in the test environment and what is worth testing is not what a real one
// does but what the service does around it: which answer belongs to which request, what a hang
// turns into, and that a dead worker is replaced rather than waited on.

class FakeWorker {
  readonly posted: { id: number; request: unknown }[] = [];
  terminated = false;
  private readonly listeners = new Map<string, Set<(event: never) => void>>();

  addEventListener(type: string, listener: (event: never) => void): void {
    (this.listeners.get(type) ?? this.listeners.set(type, new Set()).get(type)!).add(listener);
  }

  removeEventListener(type: string, listener: (event: never) => void): void {
    this.listeners.get(type)?.delete(listener);
  }

  postMessage(message: { id: number; request: unknown }): void {
    this.posted.push(message);
  }

  terminate(): void {
    this.terminated = true;
  }

  listenerCount(type: string): number {
    return this.listeners.get(type)?.size ?? 0;
  }

  emit(type: string, event: unknown): void {
    for (const listener of [...(this.listeners.get(type) ?? [])]) {
      listener(event as never);
    }
  }
}

const result = (checked: string): SchemaCheckResult => ({
  checked,
  skipped: null,
  advice: [],
  payload: null,
});

describe('SchemaChecker', () => {
  let workers: FakeWorker[];
  let checker: SchemaChecker;

  beforeEach(() => {
    vi.useFakeTimers();
    workers = [];

    TestBed.configureTestingModule({
      providers: [
        {
          provide: SCHEMA_CHECK_WORKER,
          useValue: () => {
            const worker = new FakeWorker();
            workers.push(worker);
            return worker as unknown as Worker;
          },
        },
      ],
    });

    checker = TestBed.inject(SchemaChecker);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  const ask = (schema: string) => {
    const seen: { result?: SchemaCheckResult; error?: unknown } = {};

    const subscription = checker.check({ schema, payload: null }).subscribe({
      next: (value) => (seen.result = value),
      error: (error: unknown) => (seen.error = error),
    });

    return { seen, subscription };
  };

  it('does not start a worker until something is asked', () => {
    // So a person who opens the screen and only registers a document never downloads ajv.
    expect(workers).toHaveLength(0);

    ask('{}');

    expect(workers).toHaveLength(1);
  });

  it('sends the request with an id and hands back the answer carrying that id', () => {
    const { seen } = ask('{"a":1}');
    const [worker] = workers;
    const { id, request } = worker!.posted[0]!;

    expect(request).toEqual({ schema: '{"a":1}', payload: null });

    worker!.emit('message', { data: { id, result: result('{"a":1}') } });

    expect(seen.result?.checked).toBe('{"a":1}');
  });

  it('ignores an answer meant for another request', () => {
    // One worker serves many requests over its life. A late answer to a superseded one must
    // not be shown as the answer to the current one.
    const { seen } = ask('{"a":1}');
    const worker = workers[0]!;
    const { id } = worker.posted[0]!;

    worker.emit('message', { data: { id: id + 99, result: result('other') } });

    expect(seen.result).toBeUndefined();

    worker.emit('message', { data: { id, result: result('mine') } });

    expect(seen.result?.checked).toBe('mine');
  });

  it('reuses one worker across requests', () => {
    ask('{}');
    ask('{"b":2}');

    expect(workers).toHaveLength(1);
  });

  it('turns a hang into a timeout, stops the worker, and starts a fresh one next time', () => {
    // ajv compiles `pattern` as a regular expression, and a pathological one runs as long as it
    // likes. Leaving the worker alive would queue every later check behind the one that never
    // finishes.
    const { seen } = ask('{}');

    vi.advanceTimersByTime(SCHEMA_CHECK_TIMEOUT_MS);

    expect(seen.error).toBeInstanceOf(SchemaCheckFailure);
    expect((seen.error as SchemaCheckFailure).reason).toBe('timeout');
    expect(workers[0]!.terminated).toBe(true);

    ask('{}');

    expect(workers).toHaveLength(2);
  });

  it('does not time out a check that has answered', () => {
    const { seen } = ask('{}');
    const worker = workers[0]!;

    worker.emit('message', { data: { id: worker.posted[0]!.id, result: result('{}') } });
    vi.advanceTimersByTime(SCHEMA_CHECK_TIMEOUT_MS * 2);

    expect(seen.error).toBeUndefined();
    expect(worker.terminated).toBe(false);
  });

  it('reports a failure the worker named, as unavailable', () => {
    const { seen } = ask('{}');
    const worker = workers[0]!;

    worker.emit('message', {
      data: { id: worker.posted[0]!.id, failure: 'EvalError: eval is not allowed' },
    });

    expect((seen.error as SchemaCheckFailure).reason).toBe('unavailable');
    expect((seen.error as SchemaCheckFailure).message).toContain('EvalError');
  });

  it('replaces a worker that crashes', () => {
    const { seen } = ask('{}');

    workers[0]!.emit('error', { message: 'script error' });

    expect((seen.error as SchemaCheckFailure).reason).toBe('unavailable');
    expect(workers[0]!.terminated).toBe(true);

    ask('{}');

    expect(workers).toHaveLength(2);
  });

  it('says so when a worker cannot be created at all', () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        {
          provide: SCHEMA_CHECK_WORKER,
          useValue: () => {
            throw new Error('Workers are not available.');
          },
        },
      ],
    });

    let error: unknown;
    TestBed.inject(SchemaChecker)
      .check({ schema: '{}', payload: null })
      .subscribe({ error: (e: unknown) => (error = e) });

    expect((error as SchemaCheckFailure).reason).toBe('unavailable');
    expect((error as SchemaCheckFailure).message).toContain('Workers are not available');
  });

  it('stops listening when the caller loses interest', () => {
    const { subscription } = ask('{}');
    const worker = workers[0]!;

    expect(worker.listenerCount('message')).toBe(1);

    subscription.unsubscribe();

    expect(worker.listenerCount('message')).toBe(0);
    expect(worker.listenerCount('error')).toBe(0);
  });

  it('stops the worker when it is destroyed', () => {
    ask('{}');

    TestBed.resetTestingModule();

    expect(workers[0]!.terminated).toBe(true);
  });
});
