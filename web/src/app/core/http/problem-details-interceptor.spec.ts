import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ConcordatError } from './problem-details';
import { problemDetailsInterceptor } from './problem-details-interceptor';

// `problem-details.spec.ts` covers the mapping itself. What is left to check is that it is
// actually wired into the error channel: that nothing above the interceptor can ever be
// handed an `HttpErrorResponse`, and that a success is not disturbed on the way past.

const URL = '/v1/environments/dev/subjects';

describe('problemDetailsInterceptor', () => {
  let http: HttpClient;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([problemDetailsInterceptor])),
        provideHttpClientTesting(),
      ],
    });

    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  it('leaves a successful response alone', () => {
    let body: unknown;
    http.get(URL).subscribe((value) => (body = value));

    backend.expectOne(URL).flush([{ name: 'orders.created' }]);

    expect(body).toEqual([{ name: 'orders.created' }]);
  });

  it('replaces the transport error type entirely', () => {
    // The single guarantee the rest of the app depends on. Anything above this interceptor
    // that pattern-matches on `HttpErrorResponse` is dead code, and anything that expects a
    // `ConcordatError` is safe — including the auth interceptor's 401 check.
    let caught: unknown;
    http.get(URL).subscribe({ error: (error: unknown) => (caught = error) });

    backend.expectOne(URL).flush('<html>oops</html>', { status: 500, statusText: 'Server Error' });

    expect(caught).toBeInstanceOf(ConcordatError);
  });

  it('carries a concordatCode from the wire to the caller', () => {
    let caught: ConcordatError | undefined;
    http.get(URL).subscribe({ error: (error: ConcordatError) => (caught = error) });

    backend.expectOne(URL).flush(
      {
        type: 'https://concordat.dev/problems/subject-already-exists',
        title: 'subject_already_exists',
        status: 409,
        detail: 'orders.created is already registered.',
        concordatCode: 'subject_already_exists',
      },
      { status: 409, statusText: 'Conflict' },
    );

    expect(caught?.code).toBe('subject_already_exists');
    expect(caught?.detail).toBe('orders.created is already registered.');
  });

  it('reports a request that never reached the registry as unreachable', () => {
    // The offline case. It arrives as a `ProgressEvent`, not a response, and must not be
    // reported as an HTTP failure with status 0 — see `toConcordatError`.
    let caught: ConcordatError | undefined;
    http.get(URL).subscribe({ error: (error: ConcordatError) => (caught = error) });

    backend.expectOne(URL).error(new ProgressEvent('error'));

    expect(caught?.code).toBe('registry_unreachable');
    expect(caught?.status).toBe(0);
  });

  it('does not swallow the failure', () => {
    // It converts and rethrows. An interceptor that decided what a failure *meant* — retry,
    // sign out, show a toast — would be deciding without knowing which call failed.
    let completed = false;
    let errored = false;
    http.get(URL).subscribe({ error: () => (errored = true), complete: () => (completed = true) });

    backend.expectOne(URL).flush(null, { status: 404, statusText: 'Not Found' });

    expect(errored).toBe(true);
    expect(completed).toBe(false);
  });
});
