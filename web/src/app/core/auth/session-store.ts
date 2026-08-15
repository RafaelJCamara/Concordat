import { computed } from '@angular/core';
import { signalStore, withComputed, withMethods, withState, patchState } from '@ngrx/signals';
import { grants, SCHEMA_WRITE_SCOPES, type Scope } from '../../domain/identity/scope';

interface SessionState {
  /**
   * The bearer credential, held in memory only.
   *
   * Not `localStorage`, and not `sessionStorage`. A token in web storage is readable by any
   * script that runs on the page, so one injected script exfiltrates a registry credential
   * that is good from anywhere — and this project has already declined to ship one XSS hole
   * (ADR-006). A reload no longer signs the user out: decision 26 added an httpOnly
   * `SameSite=Strict` session cookie whose only power is handing back this same in-memory
   * credential, via `SessionApi.resume()` on startup. The credential itself still never
   * touches web storage.
   */
  readonly credential: string | null;

  /** Who is acting, as the API records it in `registeredBy` and `decidedBy`. */
  readonly actor: string | null;

  /** The scopes the API granted at sign-in. Never inferred client-side. */
  readonly scopes: readonly Scope[];

  /**
   * Whether the instance has any accounts at all, or `null` before the API has been asked.
   *
   * An unclaimed instance answers as an owner without a credential (M8.2), which is what
   * makes `docker compose up` usable immediately. The distinction matters here because the
   * app must not send someone to a sign-in page when there is nobody to sign in as.
   */
  readonly claimed: boolean | null;
}

const SIGNED_OUT = {
  credential: null,
  actor: null,
  scopes: [],
} satisfies Omit<SessionState, 'claimed'>;

/**
 * The signed-in session.
 *
 * **`canWriteSchemas` is not the security boundary** (ADR-018). Every mutating endpoint
 * checks the same scopes server-side and answers 403 regardless of caller, because the API
 * is equally reachable from the CLI, the SDKs and curl. What this buys is that the UI does
 * not render an affordance the server will refuse.
 *
 * It is derived from `scopes`, which come from the API, and never from a local guess. A
 * permissive default that nobody remembers to tighten is how a write path ships ungated.
 */
export const SessionStore = signalStore(
  { providedIn: 'root' },
  withState<SessionState>({ ...SIGNED_OUT, claimed: null }),

  withComputed((store) => ({
    /** Whether a credential is held. */
    isSignedIn: computed(() => store.credential() !== null),

    /**
     * Whether to render schema write affordances (ADR-018).
     *
     * True on an unclaimed instance with no session: the API grants owner scopes to an
     * unauthenticated caller until somebody creates an account, and hiding every button on
     * a registry that would in fact accept the write is a worse first run than the buttons
     * being there.
     */
    canWriteSchemas: computed(
      () => store.claimed() === false || grants(store.scopes(), SCHEMA_WRITE_SCOPES),
    ),

    /**
     * Whether the app should be showing a sign-in prompt.
     *
     * Not simply "signed out": an unclaimed instance has nobody to sign in as, and asking
     * for credentials that do not exist yet is the sort of dead end that gets a product
     * closed.
     */
    needsSignIn: computed(() => store.claimed() === true && store.credential() === null),
  })),

  withMethods((store) => ({
    /** Records what `/v1/auth/status` said before anyone signed in. */
    observeInstance(state: { claimed: boolean; actor: string | null; scopes: readonly Scope[] }) {
      patchState(store, {
        claimed: state.claimed,
        actor: state.actor,
        scopes: state.scopes,
      });
    },

    /** Records a sign-in. Scopes come from the API's response, never from the client. */
    signIn(session: { credential: string; actor: string; scopes: readonly Scope[] }): void {
      patchState(store, { ...session, claimed: true });
    },

    /**
     * Drops the credential.
     *
     * Called by the auth interceptor on a 401 so a credential the registry has already
     * rejected is not replayed on every subsequent request — which is what turns one
     * expired token into a burst of failures across every open panel.
     *
     * `claimed` deliberately survives: whether the instance has accounts is a fact about the
     * server, not about this session, and forgetting it would send the user back to a
     * first-run experience they have already left.
     */
    expire(): void {
      patchState(store, SIGNED_OUT);
    },
  })),
);
