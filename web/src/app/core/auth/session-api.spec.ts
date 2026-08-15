import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../config/app-config';
import { SessionApi } from './session-api';

/**
 * The two session-cookie routes (decision 26).
 *
 * The credential is held in memory only — `localStorage` is readable by any script on the page
 * and ADR-006 already declined that trade — so a reload has nothing left except an httpOnly
 * cookie no script can read. `withCredentials` is what makes the browser send it, and getting
 * that wrong fails silently: sign-in still works and only the reload keeps failing.
 */
describe('SessionApi', () => {
  let api: SessionApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideConcordatConfig({ defaultEnvironment: 'dev' }),
      ],
    });

    api = TestBed.inject(SessionApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('asks for the session with credentials attached', () => {
    api.resume().subscribe();

    const request = http.expectOne((r) => r.url.endsWith('/auth/resume'));

    // Without this the browser omits the cookie cross-origin and the reload keeps signing you
    // out — while sign-in itself carries on working, so nothing looks broken.
    expect(request.request.withCredentials).toBe(true);
    request.flush({ credential: 'cdt_x', actor: 'alice', scopes: ['subject:read'] });
  });

  it('drops a scope this build does not know', () =>
    new Promise<void>((done) => {
      api.resume().subscribe((session) => {
        // An unrecognised string compared against a closed union would silently never match,
        // so the affordance would be hidden anyway — dropping it makes that deliberate.
        expect(session?.scopes).toEqual(['subject:read']);
        done();
      });

      http
        .expectOne((r) => r.url.endsWith('/auth/resume'))
        .flush({
          credential: 'cdt_x',
          actor: 'alice',
          scopes: ['subject:read', 'subject:teleport'],
        });
    }));

  it('treats a 204 as "no session" rather than a failure', () =>
    new Promise<void>((done) => {
      // The app must probe on every startup -- it cannot read an httpOnly cookie to find out
      // whether probing is worthwhile -- so treating signed-out as an error logged a console
      // error on every signed-out page load of a perfectly healthy app. A browser test
      // asserting a clean console is what surfaced it.
      api.resume().subscribe((session) => {
        expect(session).toBeNull();
        done();
      });

      http
        .expectOne((r) => r.url.endsWith('/auth/resume'))
        .flush(null, { status: 204, statusText: 'No Content' });
    }));

  it('signs out with credentials attached, because script cannot delete an httpOnly cookie', () => {
    api.signOut().subscribe();

    const request = http.expectOne((r) => r.url.endsWith('/auth/signout'));

    expect(request.request.withCredentials).toBe(true);
    request.flush(null);
  });
});
