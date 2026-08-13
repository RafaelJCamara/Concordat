import { provideBrowserGlobalErrorListeners, type ApplicationConfig } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding, withInMemoryScrolling } from '@angular/router';
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
  ],
};
