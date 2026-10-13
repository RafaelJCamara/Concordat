/// <reference lib="webworker" />

import type { SchemaCheckRequest } from '../../../domain/registry/schema-check';
import { checkSchema } from './schema-check-engine';

/**
 * The worker's whole job: a request in, an answer out.
 *
 * Every reply carries the request's `id`, because one worker serves many requests over its
 * life and a late answer to a superseded one must not be mistaken for the current answer.
 * A thrown error is reported rather than left to the worker's `error` event, which carries a
 * message and nothing else — and the one failure worth naming precisely is `ajv` being unable
 * to compile at all because the worker's own policy forbids `eval`.
 */
addEventListener('message', (event: MessageEvent<{ id: number; request: SchemaCheckRequest }>) => {
  const { id, request } = event.data;

  try {
    postMessage({ id, result: checkSchema(request) });
  } catch (error) {
    postMessage({ id, failure: error instanceof Error ? error.message : String(error) });
  }
});
