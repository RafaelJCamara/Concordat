import { HttpErrorResponse } from '@angular/common/http';
import type { ConcordatCode } from './concordat-codes';

/**
 * An RFC 9457 Problem Details body as the registry writes it.
 *
 * `concordatCode` is an extension member, and it is the part that matters. HTTP status is
 * coarse — a name collision, a retired subject and an incompatible schema all answer 409 —
 * so a client that branches on status alone cannot tell them apart. See
 * `ProblemDetailsMapping.cs`, which exists to make that point.
 */
export interface ProblemDetailsBody {
  readonly type?: string;
  readonly title?: string;
  readonly status?: number;
  readonly detail?: string;
  readonly instance?: string;
  readonly concordatCode?: string;
  readonly [member: string]: unknown;
}

/**
 * A failed API call, in the shape the rest of the app is allowed to see.
 *
 * An `Error` subclass rather than a plain object so it travels the RxJS error channel and
 * keeps a stack. Everything above the interceptor deals in this; nothing above the
 * interceptor should ever import `HttpErrorResponse`.
 */
export class ConcordatError extends Error {
  /** The HTTP status, or 0 when the request never got a response. */
  readonly status: number;

  /** The stable code to branch on. */
  readonly code: ConcordatCode;

  /** The registry's human-readable explanation. Always safe to show. */
  readonly detail: string;

  /** The problem type URI, when the response carried one. */
  readonly type: string | null;

  /**
   * Extension members beyond the RFC's own, for example `breakingChanges` on a rejected
   * registration. Untyped here: which extensions accompany which code is a per-endpoint
   * concern, and the data-access layer that made the call is the only place that knows.
   */
  readonly extensions: Readonly<Record<string, unknown>>;

  constructor(init: {
    status: number;
    code: ConcordatCode;
    detail: string;
    type?: string | null;
    extensions?: Record<string, unknown>;
    cause?: unknown;
  }) {
    super(init.detail, { cause: init.cause });
    this.name = 'ConcordatError';
    this.status = init.status;
    this.code = init.code;
    this.detail = init.detail;
    this.type = init.type ?? null;
    this.extensions = init.extensions ?? {};
  }
}

/** Narrows an unknown rejection to a mapped API failure. */
export function isConcordatError(error: unknown): error is ConcordatError {
  return error instanceof ConcordatError;
}

const RFC_MEMBERS = new Set(['type', 'title', 'status', 'detail', 'instance', 'concordatCode']);

/**
 * Maps whatever HttpClient rejected with onto a `ConcordatError`.
 *
 * Three cases, and the middle one is the one that bites. A proxy or a load balancer in
 * front of the registry answers with HTML, not `application/problem+json`; if that path
 * threw something shaped differently from the others, every caller would need a second
 * branch for "the error is not the error type". So a non-Problem body still produces a
 * `ConcordatError`, carrying `registry_refused` and the status line as its detail.
 */
export function toConcordatError(error: unknown): ConcordatError {
  if (error instanceof ConcordatError) {
    return error;
  }

  if (!(error instanceof HttpErrorResponse)) {
    return new ConcordatError({
      status: 0,
      code: 'registry_refused',
      detail: error instanceof Error ? error.message : 'The request failed for an unknown reason.',
      cause: error,
    });
  }

  // Status 0 means the request never reached a server: offline, DNS, TLS, a blocked
  // cross-origin request. Reporting that as a 0-status HTTP failure invites a caller to
  // treat it as a client error and stop retrying, which is exactly wrong.
  if (error.status === 0) {
    return new ConcordatError({
      status: 0,
      code: 'registry_unreachable',
      detail: `Could not reach the registry at ${error.url ?? 'the configured address'}.`,
      cause: error,
    });
  }

  const body = asProblemBody(error.error);

  if (body?.concordatCode === undefined) {
    return new ConcordatError({
      status: error.status,
      code: 'registry_refused',
      detail:
        body?.detail ??
        `The registry answered ${error.status} ${error.statusText || 'without an explanation'}.`,
      type: body?.type ?? null,
      cause: error,
    });
  }

  const extensions: Record<string, unknown> = {};
  for (const [member, value] of Object.entries(body)) {
    if (!RFC_MEMBERS.has(member)) {
      extensions[member] = value;
    }
  }

  return new ConcordatError({
    status: body.status ?? error.status,
    code: body.concordatCode,
    // `title` is the code repeated; `detail` is the sentence a human can act on.
    detail: body.detail ?? body.title ?? `The registry refused with ${body.concordatCode}.`,
    type: body.type ?? null,
    extensions,
    cause: error,
  });
}

function asProblemBody(value: unknown): ProblemDetailsBody | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as ProblemDetailsBody)
    : null;
}
