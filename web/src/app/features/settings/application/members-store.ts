import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, exhaustMap, mergeMap, of, pipe, switchMap, tap } from 'rxjs';
import { toConcordatError, type ConcordatError } from '../../../core/http/problem-details';
import type { Member, Role } from '../../../domain/identity/member';
import { IdentityApi, type NewMember } from '../data-access/identity-api';

interface MembersState {
  readonly members: readonly Member[];
  readonly loading: boolean;
  readonly loaded: boolean;
  readonly error: ConcordatError | null;
  readonly adding: boolean;
  /** Why the last add was refused, apart from `error` so a refusal does not blank the list. */
  readonly addError: ConcordatError | null;
  /** The member the last add created, so the screen can say so. */
  readonly added: Member | null;
  /** Which members have a role change in flight. */
  readonly changing: readonly string[];
  /** Why the last role change for each member was refused. */
  readonly roleErrors: Readonly<Record<string, ConcordatError>>;
  /** Which members have a password reset or a disabling in flight. One at a time each. */
  readonly acting: readonly string[];
  /** Why the last reset or disabling for each member was refused. */
  readonly accountErrors: Readonly<Record<string, ConcordatError>>;
  /** What the last reset, disabling or enabling did, in the registry's numbers, or null. */
  readonly notice: AccountNotice | null;
}

/** What an account change did, for the screen to say. */
export interface AccountNotice {
  readonly kind: 'reset' | 'disabled' | 'enabled';
  readonly email: string;
  /** Sessions ended by a reset, or sessions and keys revoked by a disabling. Zero for an enabling. */
  readonly count: number;
}

const INITIAL: MembersState = {
  members: [],
  loading: false,
  loaded: false,
  error: null,
  adding: false,
  addError: null,
  added: null,
  changing: [],
  roleErrors: {},
  acting: [],
  accountErrors: {},
  notice: null,
};

/**
 * The organisation's members, and adding one or changing a role.
 *
 * <b>A new member is inserted from the registry's answer</b> and a role is applied from its
 * answer, never ahead of it. Whether a role change is allowed — an owner cannot demote
 * themselves, because an instance whose last owner is a reader cannot be administered by
 * anybody — is the registry's to say, and a screen that showed the change before hearing would
 * show a demotion that did not happen.
 *
 * <b>Adding uses `exhaustMap`</b>: cancelling an in-flight `POST` on a double click abandons
 * only the browser's half of a request the registry still processes, and the second click
 * would then be refused as a duplicate of an account that was in fact created.
 *
 * <b>The initial password is passed through and never kept.</b> It is an argument to the call,
 * not state, so nothing here could show it back. The same goes for a temporary one, when an
 * administrator resets a password.
 *
 * <b>A reset or a disabling is applied from the registry's answer too</b>, with the registry's own
 * count of what it ended, and a refusal (the last owner, your own account) stays with the member it
 * concerns instead of blanking the list. Each member is acted on one action at a time.
 */
export const MembersStore = signalStore(
  withState<MembersState>(INITIAL),
  withComputed(({ loaded, loading, members }) => ({
    /** True only once a load has completed and found nothing — never while loading. */
    isEmpty: computed(() => loaded() && !loading() && members().length === 0),
  })),
  withMethods((store) => {
    const api = inject(IdentityApi);

    const emailOf = (userId: string): string =>
      store.members().find((m) => m.userId === userId)?.email ?? userId;

    // An account action starts by clearing what the last one said about this member, and ends by
    // letting them be acted on again.
    const begin = (userId: string): void => {
      const { [userId]: _cleared, ...remaining } = store.accountErrors();
      patchState(store, {
        acting: [...store.acting(), userId],
        accountErrors: remaining,
        notice: null,
      });
    };

    const finish = (userId: string): void =>
      patchState(store, { acting: store.acting().filter((id) => id !== userId) });

    const refuse = (userId: string, error: unknown) => {
      patchState(store, {
        acting: store.acting().filter((id) => id !== userId),
        accountErrors: { ...store.accountErrors(), [userId]: toConcordatError(error) },
      });

      return of(null);
    };

    return {
      load: rxMethod<void>(
        pipe(
          tap(() => patchState(store, { loading: true, error: null })),
          switchMap(() =>
            api.listMembers().pipe(
              tap((members) =>
                patchState(store, { members: byEmail(members), loading: false, loaded: true }),
              ),
              catchError((error: unknown) => {
                patchState(store, {
                  members: [],
                  loading: false,
                  loaded: true,
                  error: toConcordatError(error),
                });
                return of(null);
              }),
            ),
          ),
        ),
      ),

      add: rxMethod<NewMember>(
        pipe(
          tap(() => patchState(store, { adding: true, addError: null, added: null })),
          exhaustMap((member) =>
            api.addMember(member).pipe(
              tap((created) =>
                patchState(store, {
                  members: byEmail([...store.members(), created]),
                  adding: false,
                  added: created,
                }),
              ),
              catchError((error: unknown) => {
                patchState(store, { adding: false, addError: toConcordatError(error) });
                return of(null);
              }),
            ),
          ),
        ),
      ),

      changeRole: rxMethod<{ userId: string; role: Role }>(
        pipe(
          mergeMap(({ userId, role }) => {
            // One change per member at a time; different members may overlap.
            if (store.changing().includes(userId)) {
              return of(null);
            }

            const { [userId]: _cleared, ...remaining } = store.roleErrors();
            patchState(store, {
              changing: [...store.changing(), userId],
              roleErrors: remaining,
            });

            return api.changeRole(userId, role).pipe(
              tap((changed) =>
                patchState(store, {
                  members: store
                    .members()
                    .map((m) => (m.userId === changed.userId ? { ...m, role: changed.role } : m)),
                  changing: store.changing().filter((id) => id !== userId),
                }),
              ),
              catchError((error: unknown) => {
                patchState(store, {
                  changing: store.changing().filter((id) => id !== userId),
                  roleErrors: { ...store.roleErrors(), [userId]: toConcordatError(error) },
                });
                return of(null);
              }),
            );
          }),
        ),
      ),

      /**
       * Sets a member's password to a temporary one they must replace (ADR-048).
       *
       * One action per member at a time, and the same rules as a role change: nothing is shown ahead
       * of the registry's answer, and a refusal stays with the member it concerns. The password is an
       * argument to the call and nothing here holds it.
       */
      resetPassword: rxMethod<{ userId: string; password: string }>(
        pipe(
          mergeMap(({ userId, password }) => {
            if (store.acting().includes(userId)) {
              return of(null);
            }

            begin(userId);

            return api.resetPassword(userId, password).pipe(
              tap((answer) => {
                patchState(store, {
                  members: store
                    .members()
                    .map((m) =>
                      m.userId === answer.userId
                        ? { ...m, mustChangePassword: answer.mustChangePassword }
                        : m,
                    ),
                  notice: {
                    kind: 'reset',
                    email: emailOf(userId),
                    count: answer.sessionsSignedOut,
                  },
                });
                finish(userId);
              }),
              catchError((error: unknown) => refuse(userId, error)),
            );
          }),
        ),
      ),

      /**
       * Disables or enables a member (ADR-048).
       *
       * The flag shown is the one the registry answers, which differs from the one asked for only
       * when somebody else got there first, and it is the registry that knows.
       */
      setDisabled: rxMethod<{ userId: string; disabled: boolean }>(
        pipe(
          mergeMap(({ userId, disabled }) => {
            if (store.acting().includes(userId)) {
              return of(null);
            }

            begin(userId);

            return api.setDisabled(userId, disabled).pipe(
              tap((answer) => {
                patchState(store, {
                  members: store
                    .members()
                    .map((m) =>
                      m.userId === answer.userId ? { ...m, disabled: answer.disabled } : m,
                    ),
                  notice: {
                    kind: answer.disabled ? 'disabled' : 'enabled',
                    email: emailOf(userId),
                    count: answer.credentialsRevoked,
                  },
                });
                finish(userId);
              }),
              catchError((error: unknown) => refuse(userId, error)),
            );
          }),
        ),
      ),
    };
  }),
);

function byEmail(members: readonly Member[]): readonly Member[] {
  return [...members].sort((a, b) => a.email.localeCompare(b.email));
}
