import { signalStore, withMethods, withState, patchState } from '@ngrx/signals';
import type { Scope } from '../../domain/identity/scope';

interface SessionState {
  /**
   * The bearer credential, held in memory only.
   *
   * Not `localStorage`, and not `sessionStorage`. A token in web storage is readable by any
   * script that runs on the page, so one injected script exfiltrates a registry credential
   * that is good from anywhere — and this project has already declined to ship one XSS hole
   * (ADR-006). The cost is that a reload signs the user out, which is the right trade until
   * M8 decides between a refresh flow and an httpOnly cookie. See NOTES-FOR-INTEGRATION.md.
   */
  readonly credential: string | null;

  /** Who is acting, as the API records it in `registeredBy` and `decidedBy`. */
  readonly actor: string | null;

  /** The scopes the API granted at sign-in. Never inferred client-side. */
  readonly scopes: readonly Scope[];
}

const SIGNED_OUT: SessionState = {
  credential: null,
  actor: null,
  scopes: [],
};

/**
 * The signed-in session.
 *
 * Empty on a cold start. M4.1 has no sign-in flow and the self-hosted API does not yet
 * require a credential, so the auth interceptor sends nothing and the registry serves the
 * request — which is the correct behaviour for the single-user profile, not an oversight.
 *
 * **M4.2 hangs `canWriteSchemas` off `scopes` here** (ADR-018), consumed by the
 * `*cdIfScope` directive and `scopeGuard`. It is deliberately absent rather than stubbed
 * `true`: a permissive default that nobody remembers to tighten is how a write path ships
 * ungated, and DESIGN §9 asks for one source of truth, not two.
 */
export const SessionStore = signalStore(
  { providedIn: 'root' },
  withState<SessionState>(SIGNED_OUT),
  withMethods((store) => ({
    /** Records a sign-in. Scopes come from the API's response, never from the client. */
    signIn(session: { credential: string; actor: string; scopes: readonly Scope[] }): void {
      patchState(store, session);
    },

    /**
     * Drops the credential.
     *
     * Called by the auth interceptor on a 401 so a credential the registry has already
     * rejected is not replayed on every subsequent request — which is what turns one
     * expired token into a burst of failures across every open panel.
     */
    expire(): void {
      patchState(store, SIGNED_OUT);
    },
  })),
);
