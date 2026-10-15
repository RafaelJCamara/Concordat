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
   * A new instance answers nobody until it is claimed (ADR-047), and the app then offers its setup
   * screen. One started with the `AllowAnonymousUntilClaimed` opt-in answers an unauthenticated caller
   * as an owner instead, which `/v1/auth/status` says by granting that caller scopes: see
   * {@link SessionStore.openInstance}.
   */
  readonly claimed: boolean | null;

  /**
   * Whether the password in force is one an administrator typed, so this session can do nothing but
   * replace it (ADR-048).
   *
   * Said by the registry, in the sign-in answer, the resumed session and `/v1/auth/status`, and
   * never worked out here. While it is true `scopes` is empty, so every guard and every affordance
   * that reads the scopes already hides what the registry would refuse; this is what lets the app
   * say why, and send the person to the one screen that works.
   */
  readonly mustChangePassword: boolean;
}

const SIGNED_OUT = {
  credential: null,
  actor: null,
  scopes: [],
  mustChangePassword: false,
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
     * Derived from the scopes the API granted, and nothing else. On an instance that answers an
     * unclaimed caller as an owner, `/v1/auth/status` grants that caller the owner scopes, so the
     * buttons appear on a registry that would in fact accept the write; on a closed one it grants
     * none, and they do not.
     */
    canWriteSchemas: computed(() => grants(store.scopes(), SCHEMA_WRITE_SCOPES)),

    /**
     * Whether this instance answers a caller with no credential as an owner: unclaimed, and the API
     * says the unclaimed caller holds scopes. Only an instance started with the opt-in does
     * (ADR-047), and it is the one state that deserves a warning on every page.
     */
    openInstance: computed(() => store.claimed() === false && store.scopes().length > 0),

    /**
     * Whether the app should be showing a sign-in prompt.
     *
     * Not simply "signed out": until `/v1/auth/status` has answered nothing is known, and an
     * instance that answers an unclaimed caller as an owner has no sign-in to ask for. A closed
     * unclaimed instance does need one: it is where the setup screen lives.
     */
    needsSignIn: computed(
      () =>
        store.claimed() !== null &&
        store.credential() === null &&
        !(store.claimed() === false && store.scopes().length > 0),
    ),
  })),

  withMethods((store) => ({
    /** Records what `/v1/auth/status` said, before anyone signed in or after a session was resumed. */
    observeInstance(state: {
      claimed: boolean;
      actor: string | null;
      scopes: readonly Scope[];
      mustChangePassword?: boolean;
    }) {
      patchState(store, {
        claimed: state.claimed,
        actor: state.actor,
        scopes: state.scopes,
        mustChangePassword: state.mustChangePassword ?? false,
      });
    },

    /** Records a sign-in. Scopes come from the API's response, never from the client. */
    signIn(session: {
      credential: string;
      actor: string;
      scopes: readonly Scope[];
      mustChangePassword?: boolean;
    }): void {
      patchState(store, {
        credential: session.credential,
        actor: session.actor,
        scopes: session.scopes,
        mustChangePassword: session.mustChangePassword ?? false,
        claimed: true,
      });
    },

    /**
     * Records that the password was replaced, and what the same credential holds now.
     *
     * The credential is not replaced and was never cut down: the registry limited it on every request
     * while the password was somebody else's, so the scopes it grants are read again, from the
     * registry, and passed in.
     */
    passwordReplaced(scopes: readonly Scope[]): void {
      patchState(store, { scopes, mustChangePassword: false });
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
