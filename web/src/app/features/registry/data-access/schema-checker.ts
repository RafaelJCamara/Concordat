import { Injectable, InjectionToken, inject, type OnDestroy } from '@angular/core';
import { Observable } from 'rxjs';
import type { SchemaCheckRequest, SchemaCheckResult } from '../../../domain/registry/schema-check';

/**
 * Why a check could not be made. Never a verdict on the schema.
 *
 * `timeout` is the one the worker exists for: `ajv` compiles `pattern` as a regular
 * expression, and a pathological one can run for as long as it likes. `unavailable` covers
 * everything else about the worker itself — it would not start, it crashed, or it could not
 * compile because the policy it was served under forbids it.
 */
export class SchemaCheckFailure extends Error {
  constructor(
    readonly reason: 'timeout' | 'unavailable',
    message: string,
  ) {
    super(message);
    this.name = 'SchemaCheckFailure';
  }
}

/** How long a check may run before the worker is terminated, in milliseconds. */
export const SCHEMA_CHECK_TIMEOUT_MS = 4_000;

/**
 * Makes the worker.
 *
 * A token so a test can supply a fake: there is no `Worker` in the test environment, and the
 * thing worth testing here — ids, timeouts, restart after a hang — is not what a real one does.
 */
export const SCHEMA_CHECK_WORKER = new InjectionToken<() => Worker>('SCHEMA_CHECK_WORKER', {
  providedIn: 'root',
  factory: () => () =>
    // Module worker, so it can import. The pattern is what the build recognises: it bundles
    // the worker, with `ajv`, into its own file, which is why `ajv` is nowhere in the chunk
    // that holds this screen.
    new Worker(new URL('./schema-check.worker', import.meta.url), { type: 'module' }),
});

/**
 * Runs `ajv` against a schema, off the page's thread.
 *
 * <b>Lazy twice over.</b> The worker is not created until the first check, so a person who
 * opens the screen and only registers a document never downloads `ajv`; and it is created in
 * the first place only because the build split it out — see `SCHEMA_CHECK_WORKER`.
 *
 * <b>A hang ends in a restart, not a wait.</b> On timeout the worker is terminated and the next
 * request gets a fresh one; leaving it alive would queue every later check behind the one that
 * never finishes.
 */
@Injectable({ providedIn: 'root' })
export class SchemaChecker implements OnDestroy {
  private readonly create = inject(SCHEMA_CHECK_WORKER);

  private worker: Worker | null = null;
  private sequence = 0;

  /** Cold: nothing starts until subscribed, and unsubscribing abandons the answer. */
  check(request: SchemaCheckRequest): Observable<SchemaCheckResult> {
    return new Observable<SchemaCheckResult>((subscriber) => {
      const id = ++this.sequence;
      let worker: Worker;

      try {
        worker = this.worker ??= this.create();
      } catch (error) {
        subscriber.error(
          new SchemaCheckFailure(
            'unavailable',
            error instanceof Error ? error.message : 'The checker could not be started.',
          ),
        );
        return undefined;
      }

      const answer = (
        event: MessageEvent<{ id: number; result?: SchemaCheckResult; failure?: string }>,
      ): void => {
        if (event.data.id !== id) {
          // Somebody else's answer, or one for a request that was abandoned.
          return;
        }

        if (event.data.result !== undefined) {
          subscriber.next(event.data.result);
          subscriber.complete();
        } else {
          subscriber.error(
            new SchemaCheckFailure('unavailable', event.data.failure ?? 'The check failed.'),
          );
        }
      };

      const crashed = (event: ErrorEvent): void => {
        this.discard();
        subscriber.error(
          new SchemaCheckFailure('unavailable', event.message || 'The checker stopped.'),
        );
      };

      const timer = setTimeout(() => {
        this.discard();
        subscriber.error(
          new SchemaCheckFailure(
            'timeout',
            'The check took too long and was stopped. A very expensive regular expression in the ' +
              'schema can do this; the registry will still judge the schema.',
          ),
        );
      }, SCHEMA_CHECK_TIMEOUT_MS);

      worker.addEventListener('message', answer);
      worker.addEventListener('error', crashed);
      worker.postMessage({ id, request });

      return () => {
        clearTimeout(timer);
        worker.removeEventListener('message', answer);
        worker.removeEventListener('error', crashed);
      };
    });
  }

  ngOnDestroy(): void {
    this.discard();
  }

  private discard(): void {
    this.worker?.terminate();
    this.worker = null;
  }
}
