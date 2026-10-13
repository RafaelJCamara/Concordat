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
 * not state, so nothing here could show it back.
 */
export const MembersStore = signalStore(
  withState<MembersState>(INITIAL),
  withComputed(({ loaded, loading, members }) => ({
    /** True only once a load has completed and found nothing — never while loading. */
    isEmpty: computed(() => loaded() && !loading() && members().length === 0),
  })),
  withMethods((store) => {
    const api = inject(IdentityApi);

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
    };
  }),
);

function byEmail(members: readonly Member[]): readonly Member[] {
  return [...members].sort((a, b) => a.email.localeCompare(b.email));
}
