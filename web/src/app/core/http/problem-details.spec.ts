import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it } from 'vitest';
import { ConcordatError, isConcordatError, toConcordatError } from './problem-details';

// Every failure in the app arrives through this function, and callers branch on `code`.
// Getting it wrong is not a cosmetic defect: a caller that reads `registry_refused` where
// the registry actually said `version_not_awaiting_approval` shows the wrong explanation
// and offers the wrong recovery.
//
// The cases are organised by what the server sent, because that is the axis on which this
// function is easy to get wrong — a well-formed Problem Details body is the easy path, and
// the three ways of not being one are where the bugs live.

function problem(status: number, body: unknown, statusText = 'Conflict'): HttpErrorResponse {
  return new HttpErrorResponse({
    status,
    statusText,
    error: body,
    url: 'https://registry.example.com/v1/environments/dev/subjects',
  });
}

describe('toConcordatError', () => {
  describe('given an RFC 9457 body', () => {
    it('branches on concordatCode rather than on status', () => {
      // HTTP status is coarse — a name collision, a retired subject and an incompatible
      // schema all answer 409. `concordatCode` is the only thing that tells them apart.
      const error = toConcordatError(
        problem(409, {
          type: 'https://concordat.dev/problems/subject-retired',
          title: 'subject_retired',
          status: 409,
          detail: 'orders.created was retired on 2026-07-01.',
          concordatCode: 'subject_retired',
        }),
      );

      expect(error.code).toBe('subject_retired');
      expect(error.status).toBe(409);
      expect(error.detail).toBe('orders.created was retired on 2026-07-01.');
      expect(error.type).toBe('https://concordat.dev/problems/subject-retired');
    });

    it('keeps extension members and leaves the RFC ones out of them', () => {
      // `breakingChanges` is how a rejected registration explains itself, and the caller
      // that made the request is the only code that knows to look for it. Copying `title`
      // or `status` in alongside it would mean every consumer has to know which keys are
      // the envelope and which are the payload.
      const error = toConcordatError(
        problem(422, {
          type: 'about:blank',
          title: 'verdict_policy_mismatch',
          status: 422,
          detail: 'The schema is not backward compatible.',
          instance: '/v1/environments/dev/subjects/orders.created/versions',
          concordatCode: 'verdict_policy_mismatch',
          breakingChanges: [{ path: '/properties/customerId', kind: 'property_removed' }],
          policy: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
        }),
      );

      expect(Object.keys(error.extensions).sort()).toEqual(['breakingChanges', 'policy']);
      expect(error.extensions['policy']).toEqual({ mode: 'BACKWARD', surface: 'WIRE_JSON' });
    });

    it('carries a code this build has never heard of straight through', () => {
      // `ConcordatCode` is deliberately an open union. A registry newer than this bundle
      // emits codes that were not in the catalogue when it was built, and swallowing them
      // into a generic failure would lose the one field that could explain the refusal.
      const error = toConcordatError(
        problem(409, {
          detail: 'The subject is frozen for the migration window.',
          concordatCode: 'subject_frozen',
        }),
      );

      expect(error.code).toBe('subject_frozen');
      expect(error.detail).toBe('The subject is frozen for the migration window.');
    });

    it('prefers the body status over the transport status', () => {
      const error = toConcordatError(
        problem(400, { status: 422, concordatCode: 'semver_not_increasing' }),
      );

      expect(error.status).toBe(422);
    });

    it('falls back to the title when there is no detail', () => {
      // `title` repeats the code and `detail` is the sentence a human can act on, so the
      // fallback is a degradation rather than an equal alternative — but a code with no
      // prose at all is worse than a repeated one.
      const error = toConcordatError(
        problem(409, { title: 'subject_already_exists', concordatCode: 'subject_already_exists' }),
      );

      expect(error.detail).toBe('subject_already_exists');
    });

    it('still says something when the body carries only a code', () => {
      const error = toConcordatError(problem(409, { concordatCode: 'subject_retired' }));

      expect(error.detail).toBe('The registry refused with subject_retired.');
    });
  });

  describe('given a body that is not Problem Details', () => {
    it('reports the status line when a proxy answers with HTML', () => {
      // The case the mapping exists for. A load balancer in front of the registry answers
      // with a page, not `application/problem+json`; if that threw something shaped
      // differently, every caller would need a second branch for "the error is not the
      // error type".
      const error = toConcordatError(problem(502, '<html>502 Bad Gateway</html>', 'Bad Gateway'));

      expect(error).toBeInstanceOf(ConcordatError);
      expect(error.code).toBe('registry_refused');
      expect(error.status).toBe(502);
      expect(error.detail).toBe('The registry answered 502 Bad Gateway.');
    });

    it('does not mistake an array for a body with members', () => {
      const error = toConcordatError(problem(500, [{ concordatCode: 'schema_malformed' }]));

      expect(error.code).toBe('registry_refused');
    });

    it('still names the status when the response carried no reason phrase', () => {
      // HTTP/2 has no reason phrase at all, and Angular substitutes `Unknown Error` for the
      // empty string before this code ever sees it — which is why `toConcordatError`'s own
      // `'without an explanation'` fallback cannot be reached through `HttpClient`. The
      // sentence a user actually gets is the one asserted here.
      const error = toConcordatError(problem(500, null, ''));

      expect(error.detail).toBe('The registry answered 500 Unknown Error.');
    });

    it('keeps a detail from a Problem-shaped body that forgot the code', () => {
      // ASP.NET Core's own unhandled-exception body has this shape. The prose is still the
      // best thing available to show, even though there is nothing to branch on.
      const error = toConcordatError(
        problem(500, {
          type: 'https://tools.ietf.org/html/rfc9110#section-15.6.1',
          title: 'An error occurred while processing your request.',
          status: 500,
          detail: 'The registry could not reach its store.',
        }),
      );

      expect(error.code).toBe('registry_refused');
      expect(error.detail).toBe('The registry could not reach its store.');
      expect(error.type).toBe('https://tools.ietf.org/html/rfc9110#section-15.6.1');
    });
  });

  describe('given no response at all', () => {
    it('distinguishes unreachable from refused', () => {
      // Status 0 means offline, DNS, TLS or a blocked cross-origin request — the request
      // never reached a server. Reporting it as a 0-status HTTP failure invites a caller to
      // treat it as a client error and stop retrying, which is exactly wrong.
      const error = toConcordatError(
        new HttpErrorResponse({ status: 0, url: 'https://registry.example.com/v1/subjects' }),
      );

      expect(error.code).toBe('registry_unreachable');
      expect(error.status).toBe(0);
      expect(error.detail).toContain('https://registry.example.com/v1/subjects');
    });

    it('names the address generically when even the URL is unknown', () => {
      const error = toConcordatError(new HttpErrorResponse({ status: 0 }));

      expect(error.detail).toBe('Could not reach the registry at the configured address.');
    });
  });

  describe('given something that is not an HTTP failure at all', () => {
    it('still produces a ConcordatError so callers need only one branch', () => {
      // A mapping error thrown inside a `map` operator lands here. If it arrived as itself,
      // every `catchError` in the app would have to handle two vocabularies.
      const cause = new TypeError('dtos.map is not a function');
      const error = toConcordatError(cause);

      expect(error).toBeInstanceOf(ConcordatError);
      expect(error.code).toBe('registry_refused');
      expect(error.status).toBe(0);
      expect(error.detail).toBe('dtos.map is not a function');
      expect(error.cause).toBe(cause);
    });

    it('handles a rejection that is not even an Error', () => {
      const error = toConcordatError('nope');

      expect(error.detail).toBe('The request failed for an unknown reason.');
      expect(error.cause).toBe('nope');
    });

    it('returns an existing ConcordatError unchanged', () => {
      // The interceptor chain can run the mapping more than once — the store maps again on
      // the way into its own error state. Re-wrapping would bury the original code under
      // `registry_refused` and lose the extensions with it.
      const original = new ConcordatError({
        status: 409,
        code: 'subject_retired',
        detail: 'Already retired.',
        extensions: { retiredAt: '2026-07-01' },
      });

      expect(toConcordatError(original)).toBe(original);
    });
  });
});

describe('ConcordatError', () => {
  it('is an Error, so it travels the RxJS error channel with a stack', () => {
    const error = new ConcordatError({ status: 409, code: 'subject_retired', detail: 'Retired.' });

    expect(error).toBeInstanceOf(Error);
    expect(error.name).toBe('ConcordatError');
    expect(error.message).toBe('Retired.');
    expect(error.stack).toBeTruthy();
  });

  it('defaults type and extensions rather than leaving them undefined', () => {
    const error = new ConcordatError({ status: 409, code: 'subject_retired', detail: 'Retired.' });

    expect(error.type).toBeNull();
    expect(error.extensions).toEqual({});
  });
});

describe('isConcordatError', () => {
  it('accepts a mapped failure', () => {
    expect(
      isConcordatError(new ConcordatError({ status: 404, code: 'subject_not_found', detail: '' })),
    ).toBe(true);
  });

  it('rejects the transport error it replaces', () => {
    // The auth interceptor uses this guard to decide whether a 401 signs the user out. If
    // it accepted an `HttpErrorResponse` the interceptor ordering in `app.config.ts` would
    // stop being load-bearing, and the mistake would surface as a mysterious sign-out.
    expect(isConcordatError(new HttpErrorResponse({ status: 401 }))).toBe(false);
  });

  it('rejects a plain Error', () => {
    expect(isConcordatError(new Error('boom'))).toBe(false);
  });

  it('rejects a value shaped like one but not constructed as one', () => {
    expect(isConcordatError({ status: 401, code: 'insufficient_scope', detail: '' })).toBe(false);
  });
});
