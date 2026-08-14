import { provideBrowserGlobalErrorListeners, type ApplicationConfig } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding, withInMemoryScrolling } from '@angular/router';
import { provideSessionInitializer } from './core/auth/session-initializer';
import { provideConcordatConfig } from './core/config/app-config';
import { authInterceptor } from './core/http/auth-interceptor';
import { problemDetailsInterceptor } from './core/http/problem-details-interceptor';
import { tenantInterceptor } from './core/http/tenant-interceptor';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),

    provideRouter(
      routes,
      // Route parameters bind straight to component inputs, so M4.3's `/subjects/:name`
      // screens read `name = input.required<string>()` instead of subscribing to
      // `ActivatedRoute` and remembering to unsubscribe.
      withComponentInputBinding(),
      withInMemoryScrolling({ scrollPositionRestoration: 'enabled', anchorScrolling: 'enabled' }),
    ),

    /*
     * Order matters, and not in the obvious direction. Angular runs the response chain in
     * reverse, so the last interceptor here is the first to see an error: by the time the
     * auth interceptor looks at a failure, `problemDetailsInterceptor` has already turned it
     * into a `ConcordatError`. Reorder these and the auth interceptor silently stops
     * recognising a 401, because it is suddenly holding an `HttpErrorResponse` instead.
     */
    provideHttpClient(
      withInterceptors([tenantInterceptor, authInterceptor, problemDetailsInterceptor]),
    ),

    // Same-origin, self-hosted, single tenant. A deployment that differs overrides here;
    // nothing about the API location is baked into the bundle.
    provideConcordatConfig(),

    // After the HTTP client, and it has to be: the probe is an HTTP call. It asks the API
    // whether this instance has been claimed before the first screen renders, because an
    // unclaimed one answers as an owner and the app cannot otherwise tell "signed out" from
    // "nobody has signed up". A failure is swallowed — a registry that is not up yet must
    // not stop the UI rendering.
    provideSessionInitializer(),
  ],
};
