import { DOCUMENT, inject } from '@angular/core';
import { HttpContextToken, type HttpInterceptorFn } from '@angular/common/http';
import { tap } from 'rxjs';
import { SessionStore } from '../auth/session-store';
import { CONCORDAT_CONFIG } from '../config/app-config';
import { isApiUrl } from './api-url';
import { isConcordatError } from './problem-details';

/**
 * Set on a request whose `401` means "that password was wrong" and not "your credential is dead"
 * (ADR-048).
 *
 * Changing a password answers a wrong current password the way sign-in does, as `401
 * unauthenticated`, and it is made with a credential that is perfectly good. Without this the
 * interceptor below would read it as an expiry and sign the person out for a typo. It is opt-in per
 * request and nothing else sets it, so a `401` anywhere else still ends the session; the caller
 * that sets it is responsible for asking the registry whether the credential really died.
 */
export const REFUSAL_IS_NOT_EXPIRY = new HttpContextToken<boolean>(() => false);

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
        if (
          isConcordatError(error) &&
          error.status === 401 &&
          !req.context.get(REFUSAL_IS_NOT_EXPIRY)
        ) {
          session.expire();
        }
      },
    }),
  );
};
