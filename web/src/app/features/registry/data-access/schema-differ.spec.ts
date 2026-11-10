import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { SchemaDiffRequest, SchemaDiffResult } from '../../../domain/registry/schema-diff';
import {
  SCHEMA_DIFF_TIMEOUT_MS,
  SCHEMA_DIFF_WORKER,
  SchemaDiffFailure,
  SchemaDiffer,
} from './schema-differ';

// There is no Worker in the test environment and what is worth testing is not what a real one
// does but what the service does around it: which answer belongs to which request, what a hang
// turns into, and that a dead worker is replaced rather than waited on. The diff itself is
// `schema-diff.spec.ts`.

class FakeWorker {
  readonly posted: { id: number; request: SchemaDiffRequest }[] = [];
  terminated = false;
  private readonly listeners = new Map<string, Set<(event: never) => void>>();

  addEventListener(type: string, listener: (event: never) => void): void {
    const set = this.listeners.get(type) ?? new Set<(event: never) => void>();
    set.add(listener);
    this.listeners.set(type, set);
  }

  removeEventListener(type: string, listener: (event: never) => void): void {
    this.listeners.get(type)?.delete(listener);
  }

  postMessage(message: { id: number; request: SchemaDiffRequest }): void {
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

const result = (added: number): SchemaDiffResult => ({
  status: 'ready',
  segments: [],
  lines: [],
  added,
  removed: 0,
  unplaced: [],
});

const request = (after: string): SchemaDiffRequest => ({
  format: 'json',
  before: '{}',
  after,
  findings: [],
});

describe('SchemaDiffer', () => {
  let workers: FakeWorker[];
  let differ: SchemaDiffer;

  beforeEach(() => {
    vi.useFakeTimers();
    workers = [];

    TestBed.configureTestingModule({
      providers: [
        {
          provide: SCHEMA_DIFF_WORKER,
          useValue: () => {
            const worker = new FakeWorker();
            workers.push(worker);
            return worker as unknown as Worker;
          },
        },
      ],
    });

    differ = TestBed.inject(SchemaDiffer);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  const ask = (after: string) => {
    const seen: { result?: SchemaDiffResult; error?: unknown } = {};

    const subscription = differ.compare(request(after)).subscribe({
      next: (value) => (seen.result = value),
      error: (error: unknown) => (seen.error = error),
    });

    return { seen, subscription };
  };

  const firstPost = (worker: FakeWorker) => worker.posted[0] as { id: number };

  it('does not start a worker until something is asked', () => {
    // So a reviewer who keeps the Summary view never downloads it.
    expect(workers).toHaveLength(0);

    ask('{}');

    expect(workers).toHaveLength(1);
  });

  it('sends the request with an id and hands back the answer carrying that id', () => {
    const { seen } = ask('{"a":1}');
    const worker = workers[0] as FakeWorker;

    expect(worker.posted[0]?.request).toEqual(request('{"a":1}'));

    worker.emit('message', { data: { id: firstPost(worker).id, result: result(3) } });

    expect(seen.result).toMatchObject({ status: 'ready', added: 3 });
  });

  it('ignores an answer meant for another request', () => {
    // One worker serves many requests over its life. A late answer to a superseded one must not
    // be shown as the answer to the current one.
    const { seen } = ask('{"a":1}');
    const worker = workers[0] as FakeWorker;
    const { id } = firstPost(worker);

    worker.emit('message', { data: { id: id + 99, result: result(1) } });

    expect(seen.result).toBeUndefined();

    worker.emit('message', { data: { id, result: result(2) } });

    expect(seen.result).toMatchObject({ added: 2 });
  });

  it('reuses one worker across requests', () => {
    ask('{}');
    ask('{"b":2}');

    expect(workers).toHaveLength(1);
  });

  it('turns a hang into a timeout, stops the worker, and starts a fresh one next time', () => {
    const { seen } = ask('{}');

    vi.advanceTimersByTime(SCHEMA_DIFF_TIMEOUT_MS);

    expect(seen.error).toBeInstanceOf(SchemaDiffFailure);
    expect((seen.error as SchemaDiffFailure).reason).toBe('timeout');
    expect(workers[0]?.terminated).toBe(true);

    ask('{}');

    expect(workers).toHaveLength(2);
  });

  it('waits ten seconds, as ADR-050 says, and not a moment less', () => {
    // Literal on purpose: a test that advanced by the constant would follow it wherever it went.
    const { seen } = ask('{}');

    vi.advanceTimersByTime(9_999);
    expect(seen.error).toBeUndefined();

    vi.advanceTimersByTime(1);
    expect((seen.error as SchemaDiffFailure).reason).toBe('timeout');
  });

  it('does not time out a comparison that has answered', () => {
    const { seen } = ask('{}');
    const worker = workers[0] as FakeWorker;

    worker.emit('message', { data: { id: firstPost(worker).id, result: result(0) } });
    vi.advanceTimersByTime(SCHEMA_DIFF_TIMEOUT_MS * 2);

    expect(seen.error).toBeUndefined();
    expect(worker.terminated).toBe(false);
  });

  it('reports a failure the worker named, as unavailable', () => {
    const { seen } = ask('{}');
    const worker = workers[0] as FakeWorker;

    worker.emit('message', { data: { id: firstPost(worker).id, failure: 'RangeError: oops' } });

    expect((seen.error as SchemaDiffFailure).reason).toBe('unavailable');
    expect((seen.error as SchemaDiffFailure).message).toContain('RangeError');
  });

  it('replaces a worker that crashes', () => {
    const { seen } = ask('{}');

    workers[0]?.emit('error', { message: 'script error' });

    expect((seen.error as SchemaDiffFailure).reason).toBe('unavailable');
    expect(workers[0]?.terminated).toBe(true);

    ask('{}');

    expect(workers).toHaveLength(2);
  });

  it('says so when a worker cannot be created at all', () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        {
          provide: SCHEMA_DIFF_WORKER,
          useValue: () => {
            throw new Error('Workers are not available.');
          },
        },
      ],
    });

    let error: unknown;
    TestBed.inject(SchemaDiffer)
      .compare(request('{}'))
      .subscribe({ error: (e: unknown) => (error = e) });

    expect((error as SchemaDiffFailure).reason).toBe('unavailable');
    expect((error as SchemaDiffFailure).message).toContain('Workers are not available');
  });

  it('stops listening when the caller loses interest', () => {
    const { subscription } = ask('{}');
    const worker = workers[0] as FakeWorker;

    expect(worker.listenerCount('message')).toBe(1);

    subscription.unsubscribe();

    expect(worker.listenerCount('message')).toBe(0);
    expect(worker.listenerCount('error')).toBe(0);
  });

  it('stops the worker when it is destroyed', () => {
    ask('{}');

    TestBed.resetTestingModule();

    expect(workers[0]?.terminated).toBe(true);
  });
});
