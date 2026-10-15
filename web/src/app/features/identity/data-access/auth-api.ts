import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map, type Observable } from 'rxjs';
import { CONCORDAT_CONFIG } from '../../../core/config/app-config';
import { apiRoot } from '../../../core/http/api-url';
import { SCOPES, type Scope } from '../../../domain/identity/scope';

/** What `/v1/auth/status` returns. */
interface AuthStatusDto {
  readonly claimed: boolean;
  readonly authenticated: boolean;
  readonly actor: string | null;
  readonly scopes: readonly string[];
  readonly mustChangePassword?: boolean;
}

/** What `/v1/auth/signin` returns. */
interface SignInDto {
  readonly credential: string;
  readonly actor: string;
  readonly scopes: readonly string[];
  readonly expiresAt: string;
  readonly mustChangePassword?: boolean;
}

/** Whether this instance has any accounts, and who the caller currently is. */
export interface AuthStatus {
  readonly claimed: boolean;
  readonly authenticated: boolean;
  readonly actor: string | null;
  readonly scopes: readonly Scope[];
  /** Whether the caller's password was set by an administrator and has to be replaced (ADR-048). */
  readonly mustChangePassword: boolean;
}

/** A credential and what it grants. */
export interface SignedIn {
  readonly credential: string;
  readonly actor: string;
  /** Empty while {@link mustChangePassword} is true: the credential can do nothing else yet. */
  readonly scopes: readonly Scope[];
  readonly expiresAt: Date;
  /**
   * Whether the password that was just used is one an administrator set (ADR-048). The credential is
   * good for replacing it and for nothing else until it has.
   */
  readonly mustChangePassword: boolean;
}

/**
 * Drops any scope this build does not know.
 *
 * A newer server can grant a scope this bundle has never heard of. Passing it through would
 * put an unrecognised string into a set the UI compares against a closed union, and the
 * comparison would silently never match; dropping it means the affordance is hidden, which
 * is the safe direction. **The server is still the authority** — nothing here can grant
 * anything, only decide what to render.
 */
function toScopes(scopes: readonly string[]): readonly Scope[] {
  return scopes.filter((scope): scope is Scope => (SCOPES as readonly string[]).includes(scope));
}

/**
 * The registry's identity endpoints.
 *
 * The only place in the identity feature that touches `HttpClient`, for the same reason
 * `SubjectsApi` is: one typed entry point per resource, kept honest by the ESLint boundaries
 * rule.
 */
@Injectable({ providedIn: 'root' })
export class AuthApi {
  private readonly http = inject(HttpClient);
  private readonly config = inject(CONCORDAT_CONFIG);

  /**
   * Asks whether the instance has been claimed, and who the caller is.
   *
   * Deliberately callable without a credential: a sign-in screen has to be able to find out
   * whether there is anybody to sign in as.
   */
  status(): Observable<AuthStatus> {
    return this.http.get<AuthStatusDto>(`${apiRoot(this.config)}/auth/status`).pipe(
      map((dto) => ({
        ...dto,
        scopes: toScopes(dto.scopes),
        mustChangePassword: dto.mustChangePassword === true,
      })),
    );
  }

  /**
   * Exchanges an email and password for a credential.
   *
   * `withCredentials` so the browser keeps the httpOnly session cookie the API sets alongside
   * the credential (decision 26). Without it a cross-origin dev server drops the cookie and a
   * reload signs you out again — silently, because sign-in itself still works.
   */
  signIn(email: string, password: string): Observable<SignedIn> {
    return this.http
      .post<SignInDto>(
        `${apiRoot(this.config)}/auth/signin`,
        { email, password },
        { withCredentials: true },
      )
      .pipe(
        map((dto) => ({
          credential: dto.credential,
          actor: dto.actor,
          scopes: toScopes(dto.scopes),
          expiresAt: new Date(dto.expiresAt),
          mustChangePassword: dto.mustChangePassword === true,
        })),
      );
  }

  /**
   * Claims an instance that has no accounts yet. Works exactly once.
   *
   * `token` is the deployment's setup token, when its operator set one (`CONCORDAT_BOOTSTRAP_TOKEN`):
   * possession of the deployment, and not just reaching it, is what authorises the claim. It is left
   * out of the body when there is none, so an instance that does not ask for one is sent what it
   * always was.
   */
  bootstrap(
    email: string,
    password: string,
    displayName?: string,
    token?: string,
  ): Observable<void> {
    return this.http
      .post<unknown>(`${apiRoot(this.config)}/auth/bootstrap`, {
        email,
        password,
        displayName: displayName ?? null,
        ...(token === undefined || token === '' ? {} : { token }),
      })
      .pipe(map(() => undefined));
  }
}
