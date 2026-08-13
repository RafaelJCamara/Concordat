import { DOCUMENT, inject } from '@angular/core';
import type { HttpInterceptorFn } from '@angular/common/http';
import { tap } from 'rxjs';
import { SessionStore } from '../auth/session-store';
import { CONCORDAT_CONFIG } from '../config/app-config';
import { isApiUrl } from './api-url';
import { isConcordatError } from './problem-details';

/**
 * Attaches the session credential to Concordat API requests, and only to those.
 *
 * The `isApiUrl` gate is the whole point of the interceptor rather than an optimisation:
 * without it, any absolute URL that ends up going through `HttpClient` — a doc link, an
 * avatar, a schema fetched from somewhere else — receives a bearer token for the registry.
 *
 * Sending nothing when there is no credential is correct, not a fallback. The single-user
 * self-hosted profile has no sign-in, and an empty `Authorization: Bearer` header is worse
 * than no header: it looks like a malformed credential rather than an anonymous call.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const config = inject(CONCORDAT_CONFIG);
  const session = inject(SessionStore);
  const documentBase = inject(DOCUMENT).baseURI;

  if (!isApiUrl(req.url, config, documentBase)) {
    return next(req);
  }

  const credential = session.credential();
  const outbound = credential
    ? req.clone({ setHeaders: { Authorization: `Bearer ${credential}` } })
    : req;

  return next(outbound).pipe(
    tap({
      error: (error: unknown) => {
        // 401 means the registry has already decided this credential is no good. Keeping it
        // would replay it on every open panel's next poll, turning one expiry into a burst
        // of failures. 403 is left alone: that credential is valid, the caller simply lacks
        // the scope, and signing them out would hide the read surface they are entitled to
        // (ADR-018).
        if (isConcordatError(error) && error.status === 401) {
          session.expire();
        }
      },
    }),
  );
};
