import { DOCUMENT, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import {
  patchState,
  signalStore,
  withComputed,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import { SessionStore } from '../auth/session-store';
import {
  findTourTarget,
  planTour,
  reachStorage,
  readTourSeen,
  stepPermitted,
  storeTourSeen,
  TOUR_STEPS,
  type TourStep,
} from './tour';

/** How long a step on another screen is given to appear before it is left out, in milliseconds. */
const ARRIVAL_TIMEOUT = 3000;

/** How often the page is looked at while waiting. */
const ARRIVAL_POLL = 50;

/**
 * The guided tour (ADR-051): whether it has been shown, whether it is running, and where.
 *
 * <b>Seen is written when the tour starts, not when it ends.</b> Somebody who closes the tab on
 * step two has seen the tour; showing it again on the next visit is the nagging this exists to
 * avoid, and a tour that is only remembered once finished is one that a person who skips by
 * leaving gets forever.
 *
 * <b>It starts at most once for a page load whatever storage says</b> (`startedThisLoad`). With
 * storage blocked there is nothing to remember a finished tour by, and without that flag every
 * navigation that re-evaluated the start rule would begin it again.
 *
 * <b>The target element is a signal beside the state and not in it.</b> The store freezes its
 * state in development, and freezing a DOM node is not something to find out by trying.
 *
 * Whether the person is in the app is the shell's to say, since the shell is what knows the
 * URL: it calls {@link TourStore.autoStart} when it has decided and this decides the rest.
 */
export const TourStore = signalStore(
  { providedIn: 'root' },
  withState(() => ({
    /** The tour has been shown in this browser before. Read once, then kept in step. */
    seen: readTourSeen(reachStorage(inject(DOCUMENT).defaultView)),
    startedThisLoad: false,
    running: false,
    /** The steps that will show, in order. Empty when no tour is running. */
    steps: [] as readonly TourStep[],
    index: 0,
  })),
  withProps(() => ({
    /** What the current step points at, or null while it is being found or when it has none. */
    target: signal<HTMLElement | null>(null),
  })),
  withComputed(({ steps, index }) => ({
    current: computed(() => steps()[index()] ?? null),
    total: computed(() => steps().length),
    /** One-based, for a person to read. */
    position: computed(() => index() + 1),
    first: computed(() => index() === 0),
    last: computed(() => index() === steps().length - 1),
  })),
  withMethods((store) => {
    const document = inject(DOCUMENT);
    const router = inject(Router);
    const session = inject(SessionStore);

    /** Counts entries, so a step still waiting for its screen cannot land after the tour moved on. */
    let generation = 0;

    /**
     * Where the person was before the tour first moved them, to put them back; null while it has
     * not. Read at the moment of the first move and not when the tour starts: a tour that begins
     * as the app loads starts before the first navigation has finished, when `router.url` is
     * still `/` whatever the address bar says.
     */
    let origin: string | null = null;
    let wentTo: string | null = null;

    /** What had focus when the tour began, to give it back when the tour ends. */
    let returnFocusTo: HTMLElement | null = null;

    function rememberSeen(): void {
      patchState(store, { seen: true });
      storeTourSeen(reachStorage(document.defaultView));
    }

    /** Resolves with the target once it is on the page, or null when it did not turn up in time. */
    function waitForTarget(name: string): Promise<HTMLElement | null> {
      return new Promise((resolve) => {
        const started = Date.now();

        const look = () => {
          const found = findTourTarget(document, name);

          if (found || Date.now() - started >= ARRIVAL_TIMEOUT) {
            resolve(found);
          } else {
            setTimeout(look, ARRIVAL_POLL);
          }
        };

        look();
      });
    }

    /**
     * Shows the step at `index`, going to its screen first if it lives on another.
     *
     * A step whose target never appears is removed from the list and the next one in the
     * direction of travel is tried, so the counter shrinks to match instead of the tour
     * stopping on a step with nothing to point at. Running out of steps going forward ends
     * the tour; going back, there is nothing before step one, so it stays on the first that
     * can be shown.
     */
    async function enter(index: number, direction: 1 | -1): Promise<void> {
      const mine = ++generation;
      const step = store.steps()[index];

      if (!step) {
        // Walked off the end (or the list emptied): that is finishing, not an error.
        if (direction === 1) {
          void finish();
        } else if (store.steps().length > 0) {
          void enter(0, 1);
        }
        return;
      }

      if (step.route && router.url.split(/[?#]/)[0] !== step.route) {
        origin ??= router.url;
        await router.navigateByUrl(step.route);
        wentTo = router.url;
      }

      const target = await waitForTarget(step.target);

      if (mine !== generation) {
        return;
      }

      if (!target) {
        const remaining = store.steps().filter((candidate) => candidate !== step);
        patchState(store, { steps: remaining });
        // Removing a step shifts the later ones down by one: going forward the next step now
        // sits at the same index, going back the previous one is a place earlier.
        void enter(direction === 1 ? index : Math.max(0, index - 1), direction);
        return;
      }

      patchState(store, { index });
      store.target.set(target);
      // Anywhere on screen will do for a target the person has scrolled out of reach of; the
      // popover repositions itself on scroll, so this only has to bring it into view.
      target.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
    }

    async function finish(): Promise<void> {
      generation++;
      const back = origin;
      const there = wentTo;
      const focusBack = returnFocusTo;
      origin = null;
      wentTo = null;
      returnFocusTo = null;

      patchState(store, { running: false, steps: [], index: 0 });
      store.target.set(null);

      // Only if the tour took the person somewhere and they are still where it left them: a
      // person who has since gone elsewhere on their own has made their own choice.
      if (back && there && router.url === there && back !== there) {
        await router.navigateByUrl(back);
      }

      // A no-op for an element that has left the page, so there is nothing to check first.
      focusBack?.focus();
    }

    function start(): void {
      const planned = planTour(TOUR_STEPS, (step) =>
        step.route
          ? stepPermitted(step, session.scopes())
          : findTourTarget(document, step.target) !== null,
      );

      patchState(store, { startedThisLoad: true });

      if (planned.length === 0) {
        // Nothing to show, which means the shell has not drawn what the tour points at. Not
        // seen: the next visit should get the tour this one could not give.
        return;
      }

      const active = document.activeElement;
      returnFocusTo = active instanceof HTMLElement && active !== document.body ? active : null;
      origin = null;
      wentTo = null;

      rememberSeen();
      patchState(store, { running: true, steps: planned, index: 0 });
      void enter(0, 1);
    }

    return {
      /**
       * Starts the tour if this browser has not seen it and it has not started in this page load.
       * Returns whether it did.
       */
      autoStart(): boolean {
        if (store.seen() || store.startedThisLoad() || store.running()) {
          return false;
        }

        start();
        return store.running();
      },

      /** Starts the tour from the beginning, whatever has been seen: the replay. */
      start,

      next(): void {
        if (!store.running()) {
          return;
        }

        if (store.last()) {
          void finish();
        } else {
          void enter(store.index() + 1, 1);
        }
      },

      back(): void {
        if (store.running() && !store.first()) {
          void enter(store.index() - 1, -1);
        }
      },

      /** Ends the tour: its last step, Skip and Escape all come here. */
      finish,
    };
  }),
);
