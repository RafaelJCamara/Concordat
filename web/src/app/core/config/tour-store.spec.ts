import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { NavigationEnd, provideRouter, Router } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SessionStore } from '../auth/session-store';
import { TOUR_SEEN_KEY } from './tour';
import { TourStore } from './tour-store';

// What the store does that the pure rules in `tour.spec.ts` do not: remember across loads, start
// at most once per load, plan against the page it is shown, go to another screen and come back,
// and give focus back. It runs against a real router and a real document, with timers faked
// because a step on another screen is waited for.

@Component({ template: '' })
class Blank {}

/** Every target a signed-in administrator's shell and the settings tabs would draw. */
const SHELL = ['nav-dashboard', 'nav-subjects', 'nav-approvals', 'nav-contracts', 'nav-settings'];

describe('TourStore', () => {
  let router: Router;

  beforeEach(() => {
    vi.useFakeTimers();
    localStorage.clear();
    document.body.replaceChildren();

    // jsdom lays nothing out, so every element is said to take up room.
    vi.spyOn(HTMLElement.prototype, 'getClientRects').mockReturnValue({
      length: 1,
    } as DOMRectList);

    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: '**', component: Blank }])],
    });
    router = TestBed.inject(Router);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
    localStorage.clear();
    document.body.replaceChildren();
  });

  function draw(...targets: string[]): Record<string, HTMLElement> {
    const drawn: Record<string, HTMLElement> = {};

    for (const target of targets) {
      const element = document.createElement('a');
      element.dataset['tour'] = target;
      element.scrollIntoView = vi.fn();
      document.body.append(element);
      drawn[target] = element;
    }

    return drawn;
  }

  /** The help button is always there once anyone is in the app, so every plan ends on it. */
  const shell = () => draw(...SHELL, 'help');

  /** A caller holding these scopes, as sign-in would leave the session. */
  function signInWith(...scopes: ('org:admin' | 'subject:read')[]) {
    TestBed.inject(SessionStore).signIn({ credential: 'c', actor: 'a@example.com', scopes });
  }

  /** The route step's target turns up when the screen does, as the settings tabs would. */
  function tabsAppearOnArrival() {
    router.events.subscribe((event) => {
      if (event instanceof NavigationEnd && event.urlAfterRedirects === '/settings/api-keys') {
        draw('settings-api-keys');
      }
    });
  }

  const settle = () => vi.advanceTimersByTimeAsync(0);

  function ids(store: InstanceType<typeof TourStore>): string[] {
    return store.steps().map((step) => step.id);
  }

  describe('starting', () => {
    it('starts for a browser that has never seen it, on the first step', async () => {
      shell();
      signInWith('subject:read');
      const store = TestBed.inject(TourStore);

      expect(store.autoStart()).toBe(true);
      await settle();

      expect(store.running()).toBe(true);
      expect(store.current()?.id).toBe('dashboard');
      expect(store.position()).toBe(1);
      expect(store.target()).not.toBeNull();
    });

    it('remembers that it has been shown, as it starts and not as it ends', async () => {
      shell();
      signInWith();
      const store = TestBed.inject(TourStore);

      store.autoStart();
      await settle();

      // Still on step one: somebody who closes the tab here has seen the tour.
      expect(store.running()).toBe(true);
      expect(localStorage.getItem(TOUR_SEEN_KEY)).toBe('1');
      expect(store.seen()).toBe(true);
    });

    it('does not start by itself in a browser that has seen it', () => {
      localStorage.setItem(TOUR_SEEN_KEY, '1');
      shell();
      signInWith();
      const store = TestBed.inject(TourStore);

      expect(store.autoStart()).toBe(false);
      expect(store.running()).toBe(false);
    });

    it('starts again once the browser has been cleared', () => {
      localStorage.setItem(TOUR_SEEN_KEY, '1');
      localStorage.clear();
      shell();
      signInWith();

      expect(TestBed.inject(TourStore).autoStart()).toBe(true);
    });

    it('starts by itself at most once in a page load', async () => {
      shell();
      signInWith();
      const store = TestBed.inject(TourStore);

      expect(store.autoStart()).toBe(true);
      await settle();
      void store.finish();
      await settle();

      // Asked again, as every navigation that re-evaluates the shell would.
      expect(store.autoStart()).toBe(false);
      expect(store.running()).toBe(false);
    });

    it('does not nag on every navigation when storage is blocked', async () => {
      vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
        throw new DOMException('blocked', 'SecurityError');
      });
      vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
        throw new DOMException('blocked', 'SecurityError');
      });
      shell();
      signInWith();
      const store = TestBed.inject(TourStore);

      // It works: nothing throws, and the tour shows once.
      expect(store.autoStart()).toBe(true);
      await settle();
      expect(store.running()).toBe(true);

      void store.finish();
      await settle();

      expect(store.autoStart()).toBe(false);
      expect(store.autoStart()).toBe(false);
    });

    it('works when even reaching for storage throws', async () => {
      const view = document.defaultView!;
      vi.spyOn(view, 'localStorage', 'get').mockImplementation(() => {
        throw new DOMException('blocked', 'SecurityError');
      });
      shell();
      signInWith();

      const store = TestBed.inject(TourStore);

      expect(store.seen()).toBe(false);
      expect(store.autoStart()).toBe(true);
    });

    it('does not start, and does not call itself seen, when the page has nothing to point at', () => {
      signInWith();
      const store = TestBed.inject(TourStore);

      expect(store.autoStart()).toBe(false);
      expect(store.running()).toBe(false);
      // The next visit should get the tour this one could not give.
      expect(localStorage.getItem(TOUR_SEEN_KEY)).toBeNull();
    });

    it('asks for the tour once a page load even when the first ask found nothing to show', async () => {
      signInWith();
      const store = TestBed.inject(TourStore);

      // The page has not drawn the rail yet: nothing to point at, nothing started.
      expect(store.autoStart()).toBe(false);

      // It is drawn a moment later, and the shell asks again. That is a second ask in one load.
      shell();
      expect(store.autoStart()).toBe(false);
      expect(store.running()).toBe(false);
    });

    it('is started again on request, whatever has been seen', async () => {
      localStorage.setItem(TOUR_SEEN_KEY, '1');
      shell();
      signInWith();
      const store = TestBed.inject(TourStore);

      store.start();
      await settle();

      expect(store.running()).toBe(true);
      expect(store.position()).toBe(1);
    });

    it('goes back to the first step when asked for again mid-tour', async () => {
      shell();
      signInWith();
      const store = TestBed.inject(TourStore);

      store.start();
      await settle();
      store.next();
      await settle();
      expect(store.position()).toBe(2);

      store.start();
      await settle();

      expect(store.position()).toBe(1);
      expect(store.current()?.id).toBe('dashboard');
    });
  });

  describe('planning against the page', () => {
    it('shows the story in order to a person who can see all of it', async () => {
      shell();
      tabsAppearOnArrival();
      signInWith('org:admin');
      const store = TestBed.inject(TourStore);

      store.start();
      await settle();

      expect(ids(store)).toEqual([
        'dashboard',
        'subjects',
        'approvals',
        'contracts',
        'settings',
        'api-keys',
        'help',
      ]);
      expect(store.total()).toBe(7);
    });

    it('leaves out a step whose target is not on the page, and counts only the rest', async () => {
      // A shell without the approvals entry.
      draw('nav-dashboard', 'nav-subjects', 'nav-contracts', 'nav-settings', 'help');
      signInWith();
      const store = TestBed.inject(TourStore);

      store.start();
      await settle();

      expect(ids(store)).toEqual(['dashboard', 'subjects', 'contracts', 'settings', 'help']);
      expect(store.total()).toBe(5);

      // The counter walks 1..5 with no gap where approvals would have been.
      const seen: string[] = [];
      for (let i = 0; i < 5; i++) {
        seen.push(`${store.position()}/${store.total()}:${store.current()?.id}`);
        store.next();
        await settle();
      }

      expect(seen).toEqual([
        '1/5:dashboard',
        '2/5:subjects',
        '3/5:contracts',
        '4/5:settings',
        '5/5:help',
      ]);
    });

    it('leaves out a target that is in the markup but takes up no room', async () => {
      const drawn = shell();
      vi.mocked(HTMLElement.prototype.getClientRects).mockImplementation(function (
        this: HTMLElement,
      ) {
        return { length: this === drawn['nav-approvals'] ? 0 : 1 } as DOMRectList;
      });
      signInWith();
      const store = TestBed.inject(TourStore);

      store.start();
      await settle();

      expect(ids(store)).not.toContain('approvals');
    });

    it('leaves out the API keys step for a person who is not an administrator', async () => {
      shell();
      // Even if the tab were somehow on the page, the router would turn this person away.
      draw('settings-api-keys');
      signInWith('subject:read');
      const store = TestBed.inject(TourStore);

      store.start();
      await settle();

      expect(ids(store)).not.toContain('api-keys');
      expect(store.total()).toBe(6);
      expect(router.url).toBe('/');
    });

    it('does not mention a skipped step anywhere in what it shows', async () => {
      shell();
      signInWith('subject:read');
      const store = TestBed.inject(TourStore);

      store.start();
      await settle();

      const titles = store.steps().map((step) => step.title);
      expect(titles.join(' ')).not.toMatch(/API keys/);
    });
  });

  describe('moving', () => {
    async function running(...scopes: ('org:admin' | 'subject:read')[]) {
      shell();
      tabsAppearOnArrival();
      signInWith(...scopes);
      const store = TestBed.inject(TourStore);
      store.start();
      await settle();
      return store;
    }

    it('moves forward and back, and points at each step', async () => {
      const store = await running();
      const first = store.target();

      store.next();
      await settle();
      expect(store.current()?.id).toBe('subjects');
      expect(store.target()).not.toBe(first);

      store.back();
      await settle();
      expect(store.current()?.id).toBe('dashboard');
      expect(store.target()).toBe(first);
    });

    it('cannot go back from the first step', async () => {
      const store = await running();

      store.back();
      await settle();

      expect(store.position()).toBe(1);
      expect(store.running()).toBe(true);
    });

    it('ends on Next at the last step', async () => {
      const store = await running();

      for (let i = 0; i < store.total(); i++) {
        store.next();
        await settle();
      }

      expect(store.running()).toBe(false);
      expect(store.current()).toBeNull();
      expect(store.target()).toBeNull();
    });

    it('knows its first and last step', async () => {
      const store = await running();
      expect([store.first(), store.last()]).toEqual([true, false]);

      for (let i = 1; i < store.total(); i++) {
        store.next();
        await settle();
      }

      expect([store.first(), store.last()]).toEqual([false, true]);
    });

    it.each([0, 1, 3])('can be skipped at step %i', async (steps) => {
      const store = await running();

      for (let i = 0; i < steps; i++) {
        store.next();
        await settle();
      }
      void store.finish();
      await settle();

      expect(store.running()).toBe(false);
      expect(store.steps()).toEqual([]);
      expect(store.target()).toBeNull();
    });

    it('ignores Next and Back when no tour is running', async () => {
      shell();
      signInWith();
      const store = TestBed.inject(TourStore);

      store.next();
      store.back();
      await settle();

      expect(store.running()).toBe(false);
    });

    it('does not land on a step after the tour has been skipped while it was on its way', async () => {
      const store = await running('org:admin');
      // Walk to the step before API keys, then ask for it and skip before the screen answers.
      for (let i = 0; i < 4; i++) {
        store.next();
        await settle();
      }
      expect(store.current()?.id).toBe('settings');

      store.next();
      void store.finish();
      await vi.advanceTimersByTimeAsync(5000);

      expect(store.running()).toBe(false);
      expect(store.target()).toBeNull();
    });
  });

  describe('a step on another screen', () => {
    it('goes there, points at it, and takes the person back when the tour ends', async () => {
      shell();
      tabsAppearOnArrival();
      signInWith('org:admin');
      await router.navigateByUrl('/subjects');
      const store = TestBed.inject(TourStore);

      store.start();
      await settle();
      for (let i = 0; i < 5; i++) {
        store.next();
        await settle();
      }

      expect(store.current()?.id).toBe('api-keys');
      expect(router.url).toBe('/settings/api-keys');
      expect(store.target()?.dataset['tour']).toBe('settings-api-keys');

      store.next();
      await settle();
      expect(store.current()?.id).toBe('help');
      // Still there: the help step is about the shell, which is on every screen.
      expect(router.url).toBe('/settings/api-keys');

      store.next();
      await settle();

      expect(store.running()).toBe(false);
      expect(router.url).toBe('/subjects');
    });

    it('puts the person back where they were when it moved them, not where the router thought they were when it began', async () => {
      shell();
      tabsAppearOnArrival();
      signInWith('org:admin');
      const store = TestBed.inject(TourStore);

      // A tour that starts as the app loads starts before the first navigation has finished, so
      // the router still says `/` for a page that is about to be `/contracts`.
      store.start();
      await settle();
      await router.navigateByUrl('/contracts');
      for (let i = 0; i < 5; i++) {
        store.next();
        await settle();
      }
      expect(router.url).toBe('/settings/api-keys');

      void store.finish();
      await settle();

      expect(router.url).toBe('/contracts');
    });

    it('leaves a person who has gone elsewhere on their own where they went', async () => {
      shell();
      tabsAppearOnArrival();
      signInWith('org:admin');
      const store = TestBed.inject(TourStore);

      store.start();
      await settle();
      for (let i = 0; i < 5; i++) {
        store.next();
        await settle();
      }
      await router.navigateByUrl('/audit');

      void store.finish();
      await settle();

      expect(router.url).toBe('/audit');
    });

    it('does not move a person who was never taken anywhere', async () => {
      shell();
      signInWith('subject:read');
      await router.navigateByUrl('/contracts');
      const store = TestBed.inject(TourStore);

      store.start();
      await settle();
      void store.finish();
      await settle();

      expect(router.url).toBe('/contracts');
    });

    it('leaves the step out when its target never turns up, and the counter follows', async () => {
      shell();
      // No tabs: the screen opens and has no API keys tab.
      signInWith('org:admin');
      const store = TestBed.inject(TourStore);

      store.start();
      await settle();
      expect(store.total()).toBe(7);

      for (let i = 0; i < 4; i++) {
        store.next();
        await settle();
      }
      store.next();
      await vi.advanceTimersByTimeAsync(3500);

      // Skipped without a word: the next step is showing, and there are six steps now.
      expect(store.current()?.id).toBe('help');
      expect(ids(store)).not.toContain('api-keys');
      expect(store.total()).toBe(6);
      expect(store.position()).toBe(6);
      expect(store.running()).toBe(true);
    });

    it('steps over a missing step on the way back as well', async () => {
      shell();
      signInWith('org:admin');
      const store = TestBed.inject(TourStore);

      store.start();
      await settle();
      for (let i = 0; i < 4; i++) {
        store.next();
        await settle();
      }
      store.next();
      await vi.advanceTimersByTimeAsync(3500);
      expect(store.current()?.id).toBe('help');

      store.back();
      await settle();

      expect(store.current()?.id).toBe('settings');
      expect(store.position()).toBe(5);
    });

    it('steps over a step whose target has gone when it is reached going back', async () => {
      shell();
      tabsAppearOnArrival();
      signInWith('org:admin');
      const store = TestBed.inject(TourStore);

      store.start();
      await settle();
      for (let i = 0; i < 6; i++) {
        store.next();
        await settle();
      }
      expect(store.current()?.id).toBe('help');
      expect(store.total()).toBe(7);

      // The tab it pointed at is not on the screen any more when the person comes back to it.
      document.querySelector('[data-tour="settings-api-keys"]')?.remove();
      store.back();
      await vi.advanceTimersByTimeAsync(3500);

      expect(store.current()?.id).toBe('settings');
      expect(ids(store)).not.toContain('api-keys');
      expect(store.total()).toBe(6);
      expect(store.position()).toBe(5);
    });

    it('does not wait for a target forever', async () => {
      shell();
      signInWith('org:admin');
      const store = TestBed.inject(TourStore);

      store.start();
      await settle();
      for (let i = 0; i < 4; i++) {
        store.next();
        await settle();
      }
      store.next();

      // Still waiting just short of the limit...
      await vi.advanceTimersByTimeAsync(2900);
      expect(store.current()?.id).toBe('settings');

      // ...and given up on just after it.
      await vi.advanceTimersByTimeAsync(300);
      expect(store.current()?.id).toBe('help');
    });
  });

  describe('focus', () => {
    it('is given back to what had it when the tour began', async () => {
      const help = draw('help')['help'] as HTMLElement;
      draw(...SHELL);
      help.tabIndex = 0;
      help.focus();
      signInWith();
      const store = TestBed.inject(TourStore);

      store.start();
      await settle();
      // The tour's own card would have it now.
      (document.activeElement as HTMLElement).blur();
      expect(document.activeElement).toBe(document.body);

      void store.finish();
      await settle();

      expect(document.activeElement).toBe(help);
    });

    it('is not given to something that has left the page', async () => {
      const help = draw('help')['help'] as HTMLElement;
      draw(...SHELL);
      help.tabIndex = 0;
      help.focus();
      signInWith();
      const store = TestBed.inject(TourStore);

      store.start();
      await settle();
      help.remove();

      expect(() => void store.finish()).not.toThrow();
      await settle();
      expect(document.activeElement).toBe(document.body);
    });
  });
});
