// The guided tour, with no Angular in it (ADR-051).
//
// Split out from `TourStore` for the reason `theme.ts` is split from `ThemeStore`: the rules
// that decide whether a tour shows, and which of its steps, are plain functions of plain
// values, and a rule that needs a fixture, a router and a rendered shell to be asserted on is
// a rule that gets asserted on less. The store is the part that has to touch the DOM, the
// router and the timers.

import { grants, ORG_ADMIN_SCOPES, type Scope } from '../../domain/identity/scope';

/**
 * Where "this browser has been shown the tour" is remembered.
 *
 * `localStorage`, deliberately, and not the cookie the session uses: the one property wanted is
 * that clearing the site's data brings the tour back for the person who cleared it, and that is
 * exactly what a cache clear removes. It is the second thing this app keeps there, after the
 * theme (`concordat.appearance`), and like it holds nothing that is a secret or a decision.
 */
export const TOUR_SEEN_KEY = 'concordat.tour.seen';

/** The value written under {@link TOUR_SEEN_KEY}. A flag, so anything but this reads as "not seen". */
const SEEN = '1';

/**
 * Whether this browser has already been shown the tour.
 *
 * Wrapped because reading web storage throws, not returns null, when the browser has blocked
 * it. An unreadable flag is "not seen": the store then starts the tour once for the page load
 * and no more, so a blocked storage costs one tour per visit and not one per click.
 */
export function readTourSeen(storage: Storage | null): boolean {
  try {
    return storage?.getItem(TOUR_SEEN_KEY) === SEEN;
  } catch {
    return false;
  }
}

/** Remembers that the tour has been shown, silently doing nothing when storage is unavailable. */
export function storeTourSeen(storage: Storage | null): void {
  try {
    storage?.setItem(TOUR_SEEN_KEY, SEEN);
  } catch {
    /* A tour that comes back next visit beats a page that fails to render. */
  }
}

// Lives with the theme (which needs it before bootstrap); re-exported because the tour reads storage the same way.
export { reachStorage } from './theme';

/**
 * One stop on the tour.
 *
 * `target` is the value of a `data-tour` attribute, never a class or a tag: classes are
 * restyled and tags are rearranged, and an attribute that exists only to be found by the tour
 * is the one selector nobody edits for another reason.
 */
export interface TourStep {
  readonly id: string;
  /** The `data-tour` value of the element this step points at. */
  readonly target: string;
  /** What the step is about, in a few words. It is the dialog's accessible name. */
  readonly title: string;
  readonly body: string;
  /**
   * A route the step lives on, for the target that is not part of the shell.
   *
   * The tour goes there to show it and, when it ends, goes back to where the person was.
   */
  readonly route?: string;
  /**
   * The scopes that make the destination reachable (any one is enough). Without them the step
   * is not planned at all: it points at a screen the router would turn the person away from.
   */
  readonly requires?: readonly Scope[];
}

/**
 * The tour, in the order that tells it: what the registry holds, what is waiting on a person,
 * what is enforced, and how a machine gets in.
 *
 * Every target is a navigation entry the shell draws, with two exceptions that are said here
 * because they are the reason this is data and not markup: API keys is a tab of the settings
 * screens, so reaching it means going there (`route`) and only an administrator has it
 * (`requires`, ADR-018); and the last stop points at the control that brings the tour back.
 */
export const TOUR_STEPS: readonly TourStep[] = [
  {
    id: 'dashboard',
    target: 'nav-dashboard',
    title: 'The state of the registry',
    body: 'Start here. It shows what the current environment holds: its subjects and contracts, the versions waiting on a decision and the newest breaking changes.',
  },
  {
    id: 'subjects',
    target: 'nav-subjects',
    title: 'Subjects are your message contracts',
    body: 'Every message type has a subject, and every subject keeps its versions. Open one to read its schema, compare two versions and see who reads it.',
  },
  {
    id: 'approvals',
    target: 'nav-approvals',
    title: 'Breaking changes wait for a decision',
    body: 'A version that would break the services reading a subject is held, not published. It waits here until someone with permission approves or rejects it.',
  },
  {
    id: 'contracts',
    target: 'nav-contracts',
    title: 'What a route enforces',
    body: 'A contract ties a route, an exchange and a routing key, to the subjects that may travel on it, and says whether a message that does not match is only reported or refused.',
  },
  {
    id: 'settings',
    target: 'nav-settings',
    title: 'Settings',
    body: 'The environment and its default compatibility, the brokers, notifications, and your own account. Administrators also manage members here.',
  },
  {
    id: 'api-keys',
    target: 'settings-api-keys',
    route: '/settings/api-keys',
    requires: ORG_ADMIN_SCOPES,
    title: 'API keys let services in',
    body: 'Pipelines and services authenticate with a key, not a password. Issue one for each, with only the scopes it needs, and revoke it when it is no longer used.',
  },
  {
    id: 'help',
    target: 'help',
    title: 'Come back to this any time',
    body: 'Press this button whenever you want to take the tour again.',
  },
];

/** The CSS selector for a `data-tour` value. */
export function tourSelector(target: string): string {
  return `[data-tour="${target}"]`;
}

/**
 * The element a step points at, if it is there and takes up room on the page.
 *
 * "Takes up room" is what separates a target that is missing from one that is merely in the
 * markup: a `display: none` element, or one inside a hidden ancestor, has no client rects, and
 * a spotlight on it would be a hole drawn around nothing.
 */
export function findTourTarget(root: ParentNode, target: string): HTMLElement | null {
  for (const element of root.querySelectorAll<HTMLElement>(tourSelector(target))) {
    if (element.getClientRects().length > 0) {
      return element;
    }
  }

  return null;
}

/**
 * The steps that will actually be shown, in order.
 *
 * Planned once, before the first step, so the counter ("2 of 5") counts the steps that will
 * be there and not the ones that were written. A step whose destination the session cannot
 * reach, or whose target is not on the page, is left out here and never mentioned.
 */
export function planTour(
  steps: readonly TourStep[],
  reachable: (step: TourStep) => boolean,
): readonly TourStep[] {
  return steps.filter(reachable);
}

/** Whether the session holds a scope that admits it to the step's destination. */
export function stepPermitted(step: TourStep, granted: readonly Scope[]): boolean {
  return step.requires === undefined || grants(granted, step.requires);
}

/** What the shell knows when it asks whether to start the tour by itself. */
export interface TourEligibility {
  /** A credential is held: somebody is actually signed in. */
  readonly signedIn: boolean;
  /** Whether the instance has any account, or `null` before the registry has been asked. */
  readonly claimed: boolean | null;
  /** The password in force is one an administrator typed, so the only screen that works is the one that replaces it. */
  readonly mustChangePassword: boolean;
  /** The shell has been dropped for the sign-in and setup screen. */
  readonly chromeless: boolean;
}

/**
 * Whether the person is in the app, which is the only time the tour may start by itself.
 *
 * Four conditions, and each one is a screen the tour must not appear over: the setup and
 * sign-in form (`chromeless`, which is what the shell calls it), an instance nobody has
 * claimed yet (`claimed`), the forced change of a password somebody else chose
 * (`mustChangePassword`), and a visitor with no session at all (`signedIn`). An open,
 * unclaimed instance has no session and is therefore not included: it is a first-run state to
 * be dealt with, not a place to be shown around.
 */
export function inTheApp(state: TourEligibility): boolean {
  return state.signedIn && state.claimed === true && !state.mustChangePassword && !state.chromeless;
}
