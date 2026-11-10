import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { catchError, of, pipe, switchMap, tap } from 'rxjs';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { toConcordatError, type ConcordatError } from '../../../core/http/problem-details';
import type { Subject } from '../../../domain/registry/subject';
import { SubjectsApi } from '../data-access/subjects-api';
import { DEFAULT_PAGE_SIZE, pageCount, type SubjectListQuery } from './subject-list-query';

/**
 * How long typing must pause before the address changes, and so before the registry is asked.
 *
 * Long enough that "acme.orders" typed at an ordinary pace is one request and not eleven, short
 * enough that a pause reads as the answer arriving rather than as nothing happening. The screen
 * waits (the address is the screen's, ADR-052); this is where the number lives.
 */
export const SEARCH_DEBOUNCE_MS = 250;

/**
 * How many subjects a page has when nothing says otherwise (ADR-045, ADR-052). Kept under this name
 * for what already imports it.
 */
export const PAGE_SIZE = DEFAULT_PAGE_SIZE;

/** The page the registry was asked for, which it answered with nothing although the list is not empty. */
export interface OutOfRange {
  /** The page the address named. */
  readonly requested: number;
  /** The last page there is, which the screen goes to. */
  readonly last: number;
}

interface SubjectListState {
  /** What the address asked for: the next answer is to this, and the pager marks this page. */
  readonly prefix: string;
  readonly page: number;
  readonly size: number;

  /** The subjects of one page, newest first: the answer to `shown*`, not necessarily to what is asked. */
  readonly subjects: readonly Subject[];
  /**
   * How many subjects match, which exceeds `subjects` when the list is longer than a page (ADR-045);
   * null when the registry sent no count for a page, and the screen then says only that there may be
   * more (ADR-052).
   */
  readonly total: number | null;
  readonly loading: boolean;
  readonly error: ConcordatError | null;
  /** Whether a load has finished at least once, successfully or not. */
  readonly loaded: boolean;

  /**
   * The prefix, page and size `subjects` is the answer to. Not the requested ones: between a click and
   * its answer the pager says one page and the rows another, and a sentence naming "subjects 101-150"
   * or "what matched nothing" has to name what is on screen and not what was asked for.
   */
  readonly shown: string;
  readonly shownPage: number;
  readonly shownSize: number;

  /**
   * Whether the request in flight is a change to rows already on screen (another page, another
   * prefix), as opposed to a load that has nothing to show yet. The screen keeps the rows for the
   * first, dimmed, and replaces them with a skeleton for the second.
   */
  readonly searching: boolean;

  /**
   * The page asked for was past the end: the registry answered with nothing and a count that says
   * there is something. The screen goes to {@link OutOfRange.last} and says so; until it does, the
   * store stays `loading` so that no blank table is drawn for a list that is not empty.
   */
  readonly outOfRange: OutOfRange | null;
  /** The page that was asked for and does not exist, when the page now shown is the last one instead. */
  readonly clampedFrom: number | null;
}

const INITIAL: SubjectListState = {
  prefix: '',
  page: 1,
  size: DEFAULT_PAGE_SIZE,
  subjects: [],
  total: 0,
  loading: false,
  error: null,
  loaded: false,
  shown: '',
  shownPage: 1,
  shownSize: DEFAULT_PAGE_SIZE,
  searching: false,
  outOfRange: null,
  clampedFrom: null,
};

/** How `show` is asked: with rows kept on screen while the answer is awaited, or with a clean slate. */
export interface ShowOptions {
  /**
   * Discard what is on screen first. For an environment switch and for a retry, where the rows are
   * not an answer to anything the person is still asking; a page or prefix change keeps them.
   */
  readonly fresh?: boolean;
}

/**
 * The subject list, one page of it, as one screen needs it.
 *
 * Not `providedIn: 'root'`: this store is provided by the route that uses it, so leaving
 * the page drops the state. Registry data goes stale the moment somebody else registers a
 * version, and a root-provided list would show yesterday's subjects instantly on the way
 * back in and never say so. Per-route is the honest default; caching is a decision to take
 * deliberately, per screen, once there is a reason.
 *
 * **The store is told what to show; it does not decide.** The address holds the prefix, the page
 * and the size (ADR-052), the screen reads the address and calls {@link show}, and a click is a
 * change of address that arrives here the same way a reload does. That is what makes a reload, a
 * shared link and the Back button identical: there is one way in. This store decides which of two
 * overlapping questions is still wanted, and what the screen should say about the answer. It
 * never looks at a name itself, and it does not wait out a pause in typing: the screen does, before
 * it changes the address.
 *
 * **The search is the registry's** (ADR-038), and so is the order: always `newest` (ADR-049), so that
 * the most recently created subjects are on the first page with no action.
 */
export const SubjectListStore = signalStore(
  withState<SubjectListState>(INITIAL),
  withComputed(
    ({ subjects, total, loading, loaded, shown, shownPage, shownSize, error, outOfRange }) => ({
      /**
       * How many pages the whole list makes at the size of the rows on screen, or null when the registry sent
       * no count. The size on screen and not the one asked for: the count is the answer to the former, and
       * the pager renumbers when the rows do and not before.
       */
      pages: computed(() => {
        const n = total();

        return n === null ? null : pageCount(n, shownSize());
      }),

      /** The 1-based position of the first row on screen in the whole list (0 when there are none). */
      firstRow: computed(() => (subjects().length === 0 ? 0 : (shownPage() - 1) * shownSize() + 1)),

      /** The position of the last row on screen. */
      lastRow: computed(() =>
        subjects().length === 0 ? 0 : (shownPage() - 1) * shownSize() + subjects().length,
      ),

      /**
       * Whether the list goes on after the rows on screen: by the registry's count, or, when it sent
       * none, by the page being full, which is the only evidence there is (and says "maybe", not "yes").
       */
      hasMore: computed(() => {
        const n = total();

        return n === null
          ? subjects().length >= shownSize()
          : (shownPage() - 1) * shownSize() + subjects().length < n;
      }),

      /**
       * True only once a load has completed and returned nothing for *no prefix* — never while
       * loading. An empty answer to a prefix is {@link noMatches}, and is not this: "this
       * environment has no subjects" offers to create the first one and takes the search box away,
       * which is the wrong thing to do to someone whose prefix merely found nothing.
       */
      isEmpty: computed(
        () =>
          loaded() &&
          !loading() &&
          outOfRange() === null &&
          subjects().length === 0 &&
          shown() === '',
      ),
      /**
       * True when the answer on screen is the registry listing nothing for a prefix.
       *
       * Deliberately not conditional on `loading`: while the next search is awaited the previous
       * answer is still what is shown, and a screen that dropped its "nothing starts with that"
       * panel for a table with no rows in it, then put it back, would flash on every keystroke of
       * a prefix that is still finding nothing.
       */
      noMatches: computed(
        () =>
          loaded() &&
          error() === null &&
          outOfRange() === null &&
          subjects().length === 0 &&
          shown() !== '',
      ),
    }),
  ),
  withMethods((store) => {
    const api = inject(SubjectsApi);
    const environments = inject(ActiveEnvironmentStore);

    /**
     * The one pipeline every change of prefix, page, size and environment goes through.
     *
     * `switchMap` rather than `mergeMap` deliberately, and for two reasons. Switching environment
     * twice in quick succession must not leave the slower first response overwriting the second,
     * which would show one environment's subjects under another's name; and clicking on must not
     * leave the answer to page 3 landing after the answer to page 4, which would show the rows for
     * a page the pager no longer says. Switching also unsubscribes, so the abandoned request is
     * cancelled rather than merely ignored, and **a late answer can never land**: there is no
     * subscription left for it to arrive on (this replaces the epoch counter ADR-045 kept for the
     * pages it appended, which no longer exist).
     */
    const ask = rxMethod<SubjectListQuery>(
      pipe(
        switchMap((query) =>
          api
            .listSubjects(environments.name(), {
              prefix: query.prefix,
              limit: query.size,
              offset: (query.page - 1) * query.size,
              sort: 'newest',
            })
            .pipe(
              tap((answer) => {
                const empty = answer.items.length === 0;

                if (empty && query.page > 1) {
                  // Asked for a page past the end (an old link after the list shrank, an edited
                  // address). Not shown as a table with no rows: the screen is told where the list
                  // ends, and goes there. `loading` stays so that nothing is drawn in between.
                  const last =
                    answer.total === null ? query.page - 1 : pageCount(answer.total, query.size);

                  patchState(store, {
                    total: answer.total,
                    outOfRange: { requested: query.page, last: Math.min(last, query.page - 1) },
                  });
                  return;
                }

                patchState(store, {
                  subjects: answer.items,
                  total: answer.total,
                  shown: query.prefix,
                  shownPage: query.page,
                  shownSize: query.size,
                  loading: false,
                  searching: false,
                  loaded: true,
                  outOfRange: null,
                });
              }),
              catchError((error: unknown) => {
                patchState(store, {
                  subjects: [],
                  total: 0,
                  shown: query.prefix,
                  shownPage: query.page,
                  shownSize: query.size,
                  loading: false,
                  searching: false,
                  loaded: true,
                  outOfRange: null,
                  error: toConcordatError(error),
                });
                // Swallowed on purpose: the failure is now state the template renders. Letting
                // it propagate would also tear down the rxMethod's subscription, so the next
                // `show()` would do nothing at all.
                return of(null);
              }),
            ),
        ),
      ),
    );

    const current = (): SubjectListQuery => ({
      prefix: store.prefix(),
      page: store.page(),
      size: store.size(),
    });

    const show = (query: SubjectListQuery, options: ShowOptions = {}): void => {
      const range = store.outOfRange();
      const clamped =
        range !== null &&
        query.page === range.last &&
        query.prefix === store.prefix() &&
        query.size === store.size()
          ? range.requested
          : null;
      const keep = !options.fresh && store.loaded() && store.error() === null;

      patchState(store, {
        ...query,
        loading: true,
        searching: keep,
        error: null,
        outOfRange: null,
        clampedFrom: clamped,
        ...(keep
          ? {}
          : {
              subjects: [],
              total: 0,
              loaded: false,
              shown: '',
              shownPage: 1,
              shownSize: query.size,
            }),
      });
      ask(query);
    };

    return {
      /**
       * Shows this prefix, page and size of the active environment's subjects, and asks the
       * registry for it at once.
       *
       * The rows already on screen stay, dimmed, until the answer replaces them, unless `fresh`: a
       * click on the next page is better served by the page it is leaving than by a skeleton. A page
       * that was clamped (see {@link SubjectListState.outOfRange}) remembers which page it was
       * asked for, so the screen can say so; any other change forgets it.
       */
      show,

      /**
       * Shows the first page of every subject, from a clean slate.
       *
       * For the one other reader of this store, the create screen, which asks the list one thing (is this name
       * already taken?) and renders none of it. It is the first page of the newest subjects and not the whole
       * list, as it has been since ADR-045; the registry's 409 is what holds the line for a name it does not
       * contain.
       */
      load: (): void => {
        show({ prefix: '', page: 1, size: DEFAULT_PAGE_SIZE }, { fresh: true });
      },

      /** Asks again for what is already being shown, from a clean slate: a retry after a failure. */
      reload: (): void => {
        patchState(store, {
          loading: true,
          searching: false,
          error: null,
          outOfRange: null,
          subjects: [],
          total: 0,
          loaded: false,
        });
        ask(current());
      },
    };
  }),
);
