import { Injectable, InjectionToken, inject, type OnDestroy } from '@angular/core';
import { Observable } from 'rxjs';
import type { SchemaDiffRequest, SchemaDiffResult } from '../../../domain/registry/schema-diff';

/**
 * Why a line diff could not be made. Never a statement about the schemas.
 *
 * `timeout` is the guard behind the guards: the line and change limits stop the cases that are
 * known to be expensive, and this stops one that is expensive in a way nobody thought of.
 * `unavailable` covers everything else about the worker itself — it would not start, or it
 * crashed.
 */
export class SchemaDiffFailure extends Error {
  constructor(
    readonly reason: 'timeout' | 'unavailable',
    message: string,
  ) {
    super(message);
    this.name = 'SchemaDiffFailure';
  }
}

/**
 * How long a diff may run before the worker is terminated, in milliseconds.
 *
 * Measured, not guessed (ADR-050): the worst case the limits allow, a thousand changed lines in
 * a document at the registry's 512 KiB ceiling, takes a fraction of this. The rest is room for a
 * slow device.
 */
export const SCHEMA_DIFF_TIMEOUT_MS = 10_000;

/**
 * Makes the worker.
 *
 * A token so a test can supply a fake: there is no `Worker` in the test environment, and the
 * thing worth testing here — ids, timeouts, restart after a hang — is not what a real one does.
 */
export const SCHEMA_DIFF_WORKER = new InjectionToken<() => Worker>('SCHEMA_DIFF_WORKER', {
  providedIn: 'root',
  factory: () => () =>
    // The same pattern as `SCHEMA_CHECK_WORKER`, and the one the build recognises: it bundles
    // the worker into its own `worker-<hash>.js`, which is why the diff is nowhere in the chunk
    // that holds the approvals screen and why the web container serves it under the workers'
    // Content-Security-Policy.
    new Worker(new URL('./schema-diff.worker', import.meta.url), { type: 'module' }),
});

/**
 * Compares two schemas line by line, off the page's thread.
 *
 * <b>Lazy twice over.</b> The worker is not created until the first comparison, so a reviewer who
 * keeps the Summary view never downloads it; and a hang ends in a restart, not a wait — on timeout
 * the worker is terminated and the next request gets a fresh one.
 *
 * <b>Why a worker at all.</b> A 500 KB document is tens of thousands of lines to canonicalise,
 * intern and diff, and while a single comparison is quick when little changed, the page's thread
 * is the one that scrolls, types and answers the next click. Moving it costs one structured clone
 * each way, of two strings in and the lines on show out.
 */
@Injectable({ providedIn: 'root' })
export class SchemaDiffer implements OnDestroy {
  private readonly create = inject(SCHEMA_DIFF_WORKER);

  private worker: Worker | null = null;
  private sequence = 0;

  /** Cold: nothing starts until subscribed, and unsubscribing abandons the answer. */
  compare(request: SchemaDiffRequest): Observable<SchemaDiffResult> {
    return new Observable<SchemaDiffResult>((subscriber) => {
      const id = ++this.sequence;
      let worker: Worker;

      try {
        worker = this.worker ??= this.create();
      } catch (error) {
        subscriber.error(
          new SchemaDiffFailure(
            'unavailable',
            error instanceof Error ? error.message : 'The comparison could not be started.',
          ),
        );
        return undefined;
      }

      const answer = (
        event: MessageEvent<{ id: number; result?: SchemaDiffResult; failure?: string }>,
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
            new SchemaDiffFailure('unavailable', event.data.failure ?? 'The comparison failed.'),
          );
        }
      };

      const crashed = (event: ErrorEvent): void => {
        this.discard();
        subscriber.error(
          new SchemaDiffFailure('unavailable', event.message || 'The comparison stopped.'),
        );
      };

      const timer = setTimeout(() => {
        this.discard();
        subscriber.error(
          new SchemaDiffFailure('timeout', 'The comparison took too long and was stopped.'),
        );
      }, SCHEMA_DIFF_TIMEOUT_MS);

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
