import { computed, inject, untracked } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, debounce, of, pipe, switchMap, tap, timer } from 'rxjs';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { toConcordatError, type ConcordatError } from '../../../core/http/problem-details';
import { trimRegistryText } from '../../../domain/registry/registry-text';
import type { Subject } from '../../../domain/registry/subject';
import { SubjectsApi } from '../data-access/subjects-api';

/**
 * How long typing must pause before the registry is asked.
 *
 * Long enough that "acme.orders" typed at an ordinary pace is one request and not eleven, short
 * enough that a pause reads as the answer arriving rather than as nothing happening.
 */
export const SEARCH_DEBOUNCE_MS = 250;

interface SubjectListState {
  readonly subjects: readonly Subject[];
  readonly loading: boolean;
  readonly error: ConcordatError | null;
  /** Whether a load has finished at least once, successfully or not. */
  readonly loaded: boolean;
  /** What the person has typed, trimmed: the prefix the next request will carry. */
  readonly prefix: string;
  /**
   * The prefix `subjects` is the answer to. Not `prefix`: between a keystroke and the end of the
   * pause the box says one thing and the rows another, and a sentence naming "what matched
   * nothing" has to name what was actually asked.
   */
  readonly shown: string;
  /**
   * Whether the request in flight is a search over rows already on screen, as opposed to a load
   * that has nothing to show yet. The screen keeps the rows for the first and replaces them with
   * a skeleton for the second.
   */
  readonly searching: boolean;
}

const INITIAL: SubjectListState = {
  subjects: [],
  loading: false,
  error: null,
  loaded: false,
  prefix: '',
  shown: '',
  searching: false,
};

/** One ask of the registry. A reload is not made to wait out the pause that a keystroke is. */
interface Ask {
  readonly immediate: boolean;
}

/**
 * The subject list, as one screen needs it.
 *
 * Not `providedIn: 'root'`: this store is provided by the route that uses it, so leaving
 * the page drops the state. Registry data goes stale the moment somebody else registers a
 * version, and a root-provided list would show yesterday's subjects instantly on the way
 * back in and never say so. Per-route is the honest default; caching is a decision to take
 * deliberately, per screen, once there is a reason.
 *
 * The reference shape for the feature stores M4.3 adds: state, a computed view of it, and
 * one `rxMethod` per thing the screen can ask for — or, here, one for both of the things it can
 * ask for, because a load and a search answer the same question and must not race each other.
 *
 * **The search is the registry's** (ADR-038). This store decides when to ask, which of two
 * overlapping questions is still wanted, and what the screen should say about the answer; it
 * never looks at a name itself.
 */
export const SubjectListStore = signalStore(
  withState<SubjectListState>(INITIAL),
  withComputed(({ subjects, loading, loaded, shown, error }) => ({
    /**
     * True only once a load has completed and returned nothing for *no prefix* — never while
     * loading. An empty answer to a prefix is {@link noMatches}, and is not this: "this
     * environment has no subjects" offers to create the first one and takes the search box away,
     * which is the wrong thing to do to someone whose prefix merely found nothing.
     */
    isEmpty: computed(() => loaded() && !loading() && subjects().length === 0 && shown() === ''),
    /**
     * True when the answer on screen is the registry listing nothing for a prefix.
     *
     * Deliberately not conditional on `loading`: while the next search is awaited the previous
     * answer is still what is shown, and a screen that dropped its "nothing starts with that"
     * panel for a table with no rows in it, then put it back, would flash on every keystroke of
     * a prefix that is still finding nothing.
     */
    noMatches: computed(
      () => loaded() && error() === null && subjects().length === 0 && shown() !== '',
    ),
  })),
  withMethods((store) => {
    const api = inject(SubjectsApi);
    const environments = inject(ActiveEnvironmentStore);

    /**
     * The one pipeline both a load and a search go through.
     *
     * `switchMap` rather than `mergeMap` deliberately, and for two reasons. Switching environment
     * twice in quick succession must not leave the slower first response overwriting the second,
     * which would show one environment's subjects under another's name; and typing on must not
     * leave the answer to "ac" landing after the answer to "acme", which would show the rows for
     * a prefix the box no longer says. Switching also unsubscribes, so the abandoned request is
     * cancelled rather than merely ignored.
     *
     * `debounce` rather than `debounceTime` so that the pause is per ask: a keystroke waits, and
     * a reload does not. A reload that arrives while a keystroke is waiting replaces it — the
     * prefix is read from state when the request is made, so the one question asked is the
     * latest, and the pause that was running does not ask a second time.
     */
    const ask = rxMethod<Ask>(
      pipe(
        debounce(({ immediate }) => (immediate ? of(0) : timer(SEARCH_DEBOUNCE_MS))),
        tap(({ immediate }) =>
          patchState(store, { loading: true, searching: !immediate, error: null }),
        ),
        switchMap(() => {
          // Read now, not when the keystroke arrived: see above.
          const prefix = untracked(store.prefix);

          return api.listSubjects(environments.name(), prefix).pipe(
            tap((subjects) =>
              patchState(store, {
                subjects,
                shown: prefix,
                loading: false,
                searching: false,
                loaded: true,
              }),
            ),
            catchError((error: unknown) => {
              patchState(store, {
                subjects: [],
                shown: prefix,
                loading: false,
                searching: false,
                loaded: true,
                error: toConcordatError(error),
              });
              // Swallowed on purpose: the failure is now state the template renders. Letting
              // it propagate would also tear down the rxMethod's subscription, so the next
              // `load()` would do nothing at all.
              return of(null);
            }),
          );
        }),
      ),
    );

    return {
      /**
       * Loads the subjects for the active environment, for whatever has been typed so far, and
       * does not wait: an environment switch or a retry is not a keystroke.
       *
       * `untracked` because the screen calls this from an `effect` that means to depend on the
       * environment and on nothing else; a signal read on the way through (the prefix) would
       * quietly make every keystroke a second, immediate request.
       */
      load: (): void => {
        untracked(() => ask({ immediate: true }));
      },

      /**
       * Asks the registry for the subjects whose name begins with what was typed, once typing
       * pauses. An empty box asks for everything.
       *
       * The text is trimmed as the registry will trim it and is otherwise sent as it is, case
       * included: names are case-sensitive, so the search is. Text that no name could start with
       * is sent too, and the registry answers with an empty list; the screen says why (see
       * `checkSubjectNamePrefix`), but never decides.
       */
      search: (text: string): void => {
        const prefix = trimRegistryText(text);

        if (prefix === store.prefix()) {
          return;
        }

        patchState(store, { prefix });
        ask({ immediate: false });
      },
    };
  }),
);
