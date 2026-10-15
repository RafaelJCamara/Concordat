import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map, type Observable } from 'rxjs';
import { CONCORDAT_CONFIG } from '../config/app-config';
import { apiRoot } from '../http/api-url';
import { SCOPES, type Scope } from '../../domain/identity/scope';

/** What `/v1/auth/resume` returns. */
interface SignInDto {
  readonly credential: string;
  readonly actor: string;
  readonly scopes: readonly string[];
  readonly mustChangePassword?: boolean;
}

/** What `/v1/auth/status` returns. */
interface AuthStatusDto {
  readonly claimed: boolean;
  readonly authenticated: boolean;
  readonly actor: string | null;
  readonly scopes: readonly string[];
  readonly mustChangePassword?: boolean;
}

/** A recovered session. */
export interface ResumedSession {
  readonly credential: string;
  readonly actor: string;
  readonly scopes: readonly Scope[];
  /** Whether it can do nothing but replace a password an administrator set (ADR-048). */
  readonly mustChangePassword: boolean;
}

/** What the registry says about the credential a request carried. */
export interface CredentialStatus {
  readonly claimed: boolean;
  readonly authenticated: boolean;
  readonly actor: string | null;
  readonly scopes: readonly Scope[];
  readonly mustChangePassword: boolean;
}

/**
 * Drops any scope this build does not know, so an unrecognised string never reaches a
 * comparison against a closed union — where it would silently never match.
 */
function known(scopes: readonly string[]): Scope[] {
  return scopes.filter((scope): scope is Scope => (SCOPES as readonly string[]).includes(scope));
}

/**
 * The two session-cookie routes (decision 26), and the status read that says what the credential
 * holds once a temporary password has been replaced (ADR-048).
 *
 * <b>In `core/auth` rather than in the identity feature, because the shell and the app
 * initializer are the callers</b> — and neither may import a feature. Signing in belongs to the
 * sign-in screen and stays on `AuthApi`; recovering and discarding a session belong to whatever
 * owns the session, which is `core`.
 *
 * Both calls set `withCredentials`: the cookie is the entire point, and a cross-origin dev
 * server drops it otherwise — silently, because sign-in itself would still work and only the
 * reload would keep failing.
 */
@Injectable({ providedIn: 'root' })
export class SessionApi {
  private readonly http = inject(HttpClient);
  private readonly config = inject(CONCORDAT_CONFIG);

  /**
   * Trades the httpOnly session cookie back for a credential.
   *
   * The credential is held in memory only — `localStorage` is readable by any script on the
   * page, and ADR-006 already declined that trade. This is what stops a reload signing you out
   * without putting the token anywhere script can reach.
   *
   * Resolves to `null` when there is no session, rather than erroring. The app must probe on
   * every startup — it cannot read an httpOnly cookie to find out whether probing is worthwhile
   * — so treating "signed out" as a failure logged a console error on every signed-out page
   * load of a perfectly healthy app.
   */
  resume(): Observable<ResumedSession | null> {
    return this.http
      .post<SignInDto | null>(`${apiRoot(this.config)}/auth/resume`, null, {
        withCredentials: true,
      })
      .pipe(
        // 204 with no body is "nothing to resume", which is the ordinary signed-out answer
        // rather than a failure. The API answers 401 only when a cookie was presented and did
        // not verify.
        map((dto) =>
          dto
            ? {
                credential: dto.credential,
                actor: dto.actor,
                scopes: known(dto.scopes),
                mustChangePassword: dto.mustChangePassword === true,
              }
            : null,
        ),
      );
  }

  /**
   * Asks who the registry thinks the caller is, and what that credential holds right now.
   *
   * Where the app learns what a session may do once an administrator's temporary password has been
   * replaced: the same credential holds its scopes again, and which ones is the registry's to say.
   * Sent with the credential the interceptor attaches; with none it answers for an anonymous caller.
   */
  status(): Observable<CredentialStatus> {
    return this.http.get<AuthStatusDto>(`${apiRoot(this.config)}/auth/status`).pipe(
      map((dto) => ({
        claimed: dto.claimed,
        authenticated: dto.authenticated,
        actor: dto.actor,
        scopes: known(dto.scopes),
        mustChangePassword: dto.mustChangePassword === true,
      })),
    );
  }

  /**
   * Clears the session cookie.
   *
   * A round trip, because script cannot delete an httpOnly cookie: the property that makes the
   * cookie safe is the same one that makes signing out need the server.
   */
  signOut(): Observable<void> {
    return this.http
      .post<unknown>(`${apiRoot(this.config)}/auth/signout`, null, { withCredentials: true })
      .pipe(map(() => undefined));
  }
}
