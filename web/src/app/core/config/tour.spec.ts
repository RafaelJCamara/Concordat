import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  findTourTarget,
  inTheApp,
  planTour,
  reachStorage,
  readTourSeen,
  stepPermitted,
  storeTourSeen,
  TOUR_SEEN_KEY,
  TOUR_STEPS,
  tourSelector,
  type TourEligibility,
  type TourStep,
} from './tour';

// The rules the store and the shell lean on, as plain functions of plain values. What needs a
// router, a document and a clock is `tour-store.spec.ts`.

/** A storage that refuses every call, as a browser with site data blocked does. */
function blockedStorage(): Storage {
  const refuse = () => {
    throw new DOMException('blocked', 'SecurityError');
  };

  return { getItem: refuse, setItem: refuse } as unknown as Storage;
}

describe('the seen flag', () => {
  afterEach(() => localStorage.clear());

  it('is not seen in a browser that has never been shown the tour', () => {
    expect(readTourSeen(localStorage)).toBe(false);
  });

  it('is seen once stored, and a cleared storage forgets it', () => {
    storeTourSeen(localStorage);
    expect(readTourSeen(localStorage)).toBe(true);
    expect(localStorage.getItem(TOUR_SEEN_KEY)).toBe('1');

    // What clearing site data does, and the reason the flag lives here: the tour comes back.
    localStorage.clear();
    expect(readTourSeen(localStorage)).toBe(false);
  });

  it('reads only the value it writes, so a stray entry under the key is not "seen"', () => {
    localStorage.setItem(TOUR_SEEN_KEY, '0');
    expect(readTourSeen(localStorage)).toBe(false);

    localStorage.setItem(TOUR_SEEN_KEY, 'true');
    expect(readTourSeen(localStorage)).toBe(false);
  });

  it('is not seen, rather than an error, when storage refuses the read', () => {
    expect(readTourSeen(blockedStorage())).toBe(false);
  });

  it('does not throw when storage refuses the write', () => {
    expect(() => storeTourSeen(blockedStorage())).not.toThrow();
  });

  it('does nothing without a storage at all', () => {
    expect(readTourSeen(null)).toBe(false);
    expect(() => storeTourSeen(null)).not.toThrow();
  });

  it('reaches no storage, rather than throwing, when the browser refuses even the property', () => {
    // Chrome with site data blocked throws from the `localStorage` getter itself.
    const view = {
      get localStorage(): Storage {
        throw new DOMException('blocked', 'SecurityError');
      },
    } as unknown as Window;

    expect(reachStorage(view)).toBeNull();
    expect(reachStorage(null)).toBeNull();
    expect(reachStorage(window)).toBe(window.localStorage);
  });
});

describe('finding a target', () => {
  afterEach(() => {
    document.body.replaceChildren();
    vi.restoreAllMocks();
  });

  function element(target: string, laidOut = true): HTMLElement {
    const el = document.createElement('a');
    el.dataset['tour'] = target;
    el.dataset['laidOut'] = String(laidOut);
    document.body.append(el);
    return el;
  }

  /** jsdom lays nothing out, so which elements take up room is said by the test. */
  function layout() {
    vi.spyOn(HTMLElement.prototype, 'getClientRects').mockImplementation(function (
      this: HTMLElement,
    ) {
      return { length: this.dataset['laidOut'] === 'true' ? 1 : 0 } as DOMRectList;
    });
  }

  it('selects by the data-tour attribute and nothing else', () => {
    expect(tourSelector('nav-subjects')).toBe('[data-tour="nav-subjects"]');
  });

  it('finds an element that takes up room', () => {
    layout();
    const el = element('nav-subjects');

    expect(findTourTarget(document, 'nav-subjects')).toBe(el);
  });

  it('is null for a target that is not on the page', () => {
    layout();
    element('nav-subjects');

    expect(findTourTarget(document, 'nav-approvals')).toBeNull();
  });

  it('is null for a target that is in the markup but takes up no room', () => {
    layout();
    element('nav-approvals', false);

    expect(findTourTarget(document, 'nav-approvals')).toBeNull();
  });

  it('prefers the element that is shown when the attribute is on two', () => {
    layout();
    element('help', false);
    const shown = element('help');

    expect(findTourTarget(document, 'help')).toBe(shown);
  });
});

describe('planning', () => {
  const step = (id: string): TourStep => ({ id, target: id, title: id, body: id });

  it('keeps the steps that can be shown, in their order', () => {
    const steps = ['a', 'b', 'c', 'd'].map(step);

    expect(planTour(steps, (s) => s.id !== 'b' && s.id !== 'd').map((s) => s.id)).toEqual([
      'a',
      'c',
    ]);
  });

  it('plans nothing when nothing can be shown', () => {
    expect(planTour(['a', 'b'].map(step), () => false)).toEqual([]);
  });

  it('lets a step without a requirement through, and holds one with it to the scopes', () => {
    const open = step('open');
    const gated: TourStep = { ...step('gated'), requires: ['org:admin'] };

    expect(stepPermitted(open, [])).toBe(true);
    expect(stepPermitted(gated, [])).toBe(false);
    expect(stepPermitted(gated, ['subject:read'])).toBe(false);
    expect(stepPermitted(gated, ['subject:read', 'org:admin'])).toBe(true);
  });
});

describe('the tour', () => {
  it('tells the story in order: dashboard, subjects, approvals, contracts, settings, API keys', () => {
    expect(TOUR_STEPS.map((s) => s.id)).toEqual([
      'dashboard',
      'subjects',
      'approvals',
      'contracts',
      'settings',
      'api-keys',
      'help',
    ]);
  });

  it('has a target, a title and some words for every step, and none twice', () => {
    for (const s of TOUR_STEPS) {
      expect(s.target).toMatch(/^[a-z][a-z-]*$/);
      expect(s.title.length).toBeGreaterThan(0);
      expect(s.body.length).toBeGreaterThan(0);
    }

    expect(new Set(TOUR_STEPS.map((s) => s.id)).size).toBe(TOUR_STEPS.length);
    expect(new Set(TOUR_STEPS.map((s) => s.target)).size).toBe(TOUR_STEPS.length);
  });

  it('sends only the API keys step to another screen, and only an administrator', () => {
    const away = TOUR_STEPS.filter((s) => s.route !== undefined);

    expect(away.map((s) => s.id)).toEqual(['api-keys']);
    expect(away[0]?.route).toBe('/settings/api-keys');
    expect(away[0]?.requires).toEqual(['org:admin']);
  });
});

describe('being in the app', () => {
  const inside: TourEligibility = {
    signedIn: true,
    claimed: true,
    mustChangePassword: false,
    chromeless: false,
  };

  it('is true for a signed-in person on a claimed instance in the shell', () => {
    expect(inTheApp(inside)).toBe(true);
  });

  it.each([
    ['nobody is signed in', { signedIn: false }],
    ['the instance has not said whether it is claimed', { claimed: null }],
    ['the instance is unclaimed, which is the setup screen', { claimed: false }],
    ['the password in force is one an administrator typed', { mustChangePassword: true }],
    ['the shell is dropped for the sign-in screen', { chromeless: true }],
  ] as const)('is false when %s', (_why, change) => {
    expect(inTheApp({ ...inside, ...change })).toBe(false);
  });
});
