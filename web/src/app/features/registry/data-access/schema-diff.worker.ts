/// <reference lib="webworker" />

import { diffSchemas, type SchemaDiffRequest } from '../../../domain/registry/schema-diff';

/**
 * The worker's whole job: two schemas in, a line diff out (ADR-050).
 *
 * The same shape as `schema-check.worker.ts`, for the same reason: every reply carries the
 * request's `id`, because one worker serves many requests over its life and a late answer to a
 * superseded one must not be mistaken for the current answer. A thrown error is reported rather
 * than left to the worker's `error` event, which carries a message and nothing else.
 *
 * Nothing here evaluates a string, so unlike the checker's worker this one would run under the
 * strictest policy there is; it is served under the workers' policy only because the build names
 * both `worker-<hash>.js` (see `docker/web/nginx.conf.template`).
 */
addEventListener('message', (event: MessageEvent<{ id: number; request: SchemaDiffRequest }>) => {
  const { id, request } = event.data;

  try {
    postMessage({ id, result: diffSchemas(request) });
  } catch (error) {
    postMessage({ id, failure: error instanceof Error ? error.message : String(error) });
  }
});
