import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ThemeStore } from './theme-store';

// `theme.spec.ts` covers the resolution rules. What is left is the part only the store
// does: subscribing to the media query so a `system` choice keeps following the OS, and
// running the effect that writes the class onto the document.

/** A `MediaQueryList` whose `matches` can be changed and announced, as the OS would. */
function fakeMediaQuery(matches: boolean) {
  const listeners = new Set<(event: MediaQueryListEvent) => void>();

  return {
    query: null as string | null,
    matches,
    addEventListener(_type: string, listener: (event: MediaQueryListEvent) => void) {
      listeners.add(listener);
    },
    removeEventListener(_type: string, listener: (event: MediaQueryListEvent) => void) {
      listeners.delete(listener);
    },
    /** What the OS does at dusk. */
    announce(next: boolean) {
      this.matches = next;
      for (const listener of [...listeners]) {
        listener({ matches: next } as MediaQueryListEvent);
      }
    },
    get listenerCount() {
      return listeners.size;
    },
  };
}

describe('ThemeStore', () => {
  const originalMatchMedia = window.matchMedia;
  let media: ReturnType<typeof fakeMediaQuery>;

  beforeEach(() => {
    media = fakeMediaQuery(false);
    window.matchMedia = ((query: string) => {
      media.query = query;
      return media as unknown as MediaQueryList;
    }) as typeof window.matchMedia;
    localStorage.clear();
    document.documentElement.classList.remove('dark');
  });

  afterEach(() => {
    window.matchMedia = originalMatchMedia;
    localStorage.clear();
    document.documentElement.classList.remove('dark');
  });

  /** Injecting is what constructs the store, so every test does it after arranging. */
  function store() {
    return TestBed.inject(ThemeStore);
  }

  it('starts on dark so a first visit sees the designed theme', () => {
    // Even on a light OS. The design is a dark developer theme and the light palette is an
    // addition to it, so `system` as a default would show most first-time visitors an
    // interface no screenshot of this product shows.
    media.matches = false;

    expect(store().appearance()).toBe('dark');
    expect(store().resolved()).toBe('dark');
  });

  it('reads the choice stored by a previous visit', () => {
    media.matches = true;
    localStorage.setItem('concordat.appearance', 'light');

    expect(store().resolved()).toBe('light');
  });

  it('follows the OS changing under a system choice, without a reload', () => {
    // The reason the media query is watched rather than read once. A user who chose
    // `system` expects the page to turn dark when their OS does at dusk; reading `matches`
    // at construction would leave them on the theme they had at breakfast.
    const theme = store();
    theme.choose('system');
    expect(theme.resolved()).toBe('light');

    media.announce(true);

    expect(theme.resolved()).toBe('dark');
  });

  it('ignores the OS once a choice has been made', () => {
    const theme = store();
    theme.choose('light');

    media.announce(true);

    expect(theme.resolved()).toBe('light');
  });

  it('goes back to following the OS when the choice returns to system', () => {
    const theme = store();
    theme.choose('dark');
    media.announce(false);
    expect(theme.resolved()).toBe('dark');

    theme.choose('system');

    expect(theme.resolved()).toBe('light');
  });

  it('persists a choice so the next load does not flash', () => {
    // `main.ts` reads this key before bootstrap. A choice that is not written is a choice
    // that reverts on reload, visibly.
    store().choose('dark');

    expect(localStorage.getItem('concordat.appearance')).toBe('dark');
  });

  it('writes the resolved theme onto the document root', () => {
    const theme = store();
    theme.choose('dark');
    TestBed.tick();

    expect(document.documentElement.classList.contains('dark')).toBe(true);

    theme.choose('light');
    TestBed.tick();

    expect(document.documentElement.classList.contains('dark')).toBe(false);
  });

  it('writes the document class when the OS changes too, not only on a choice', () => {
    // The effect reads `resolved()`, which depends on both inputs. If it tracked only the
    // stored choice, a `system` user's OS flip would update the store and leave the page
    // rendered in the old theme.
    store().choose('system');
    TestBed.tick();
    expect(document.documentElement.classList.contains('dark')).toBe(false);

    media.announce(true);
    TestBed.tick();

    expect(document.documentElement.classList.contains('dark')).toBe(true);
  });

  it('asks the OS about colour scheme specifically', () => {
    store();

    expect(media.query).toBe('(prefers-color-scheme: dark)');
  });

  it('unsubscribes from the media query when the injector is destroyed', () => {
    // The listener is registered on a global object that outlives the store. Without the
    // `DestroyRef` cleanup, every test in this file — and every hot reload in development —
    // would leave another live listener behind holding a dead signal.
    store();
    expect(media.listenerCount).toBe(1);

    TestBed.resetTestingModule();

    expect(media.listenerCount).toBe(0);
  });
});
