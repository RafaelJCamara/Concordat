import type { HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';
import { toConcordatError } from './problem-details';

/**
 * Turns every HTTP failure into a `ConcordatError` before anything else sees it.
 *
 * **This must be the innermost interceptor.** Angular runs the response chain in reverse, so
 * the interceptor registered last is the first to see an error. Put this one anywhere else
 * and the interceptors outside it — auth, in particular — would be pattern-matching on
 * `HttpErrorResponse`, and the app would have two error vocabularies with no rule about
 * which one a given `catchError` is holding. One conversion, at the boundary, once.
 *
 * It deliberately does not decide what a failure *means*. Retry, sign-out, redirect and
 * user-facing wording are all decisions that need to know which call failed, and this
 * interceptor cannot know that.
 */
export const problemDetailsInterceptor: HttpInterceptorFn = (req, next) =>
  next(req).pipe(catchError((error: unknown) => throwError(() => toConcordatError(error))));
