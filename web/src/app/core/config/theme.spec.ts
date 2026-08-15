import { describe, expect, it } from 'vitest';
import {
  applyAppearance,
  applyStoredAppearance,
  readStoredAppearance,
  resolveAppearance,
  storeAppearance,
  systemPrefersDark,
} from './theme';

// This module runs twice per page load — once from `main.ts` before bootstrap, once from
// `ThemeStore` after it — and the two must agree or the page changes theme as Angular
// starts. It is also the only code in the app that touches `localStorage`, which throws
// rather than returning null when a browser has blocked it.

/** A `Storage` that works. */
function workingStorage(initial: Record<string, string> = {}): Storage {
  const entries = new Map(Object.entries(initial));

  return {
    getItem: (key: string) => entries.get(key) ?? null,
    setItem: (key: string, value: string) => void entries.set(key, value),
    removeItem: (key: string) => void entries.delete(key),
    clear: () => entries.clear(),
    key: (index: number) => [...entries.keys()][index] ?? null,
    get length() {
      return entries.size;
    },
  };
}

/** A `Storage` that throws, as Safari's does in private browsing. */
function blockedStorage(): Storage {
  const refuse = () => {
    throw new DOMException('The operation is insecure.', 'SecurityError');
  };

  return {
    getItem: refuse,
    setItem: refuse,
    removeItem: refuse,
    clear: refuse,
    key: refuse,
    get length(): number {
      return refuse();
    },
  };
}

describe('readStoredAppearance', () => {
  it('returns the stored choice', () => {
    expect(readStoredAppearance(workingStorage({ 'concordat.appearance': 'dark' }))).toBe('dark');
  });

  it('defaults to dark when nothing is stored', () => {
    // `dark` and not `system`. The design is a dark developer theme and the light palette
    // is an addition to it, so a first visit should show the interface the product was
    // drawn as — not hand most visitors, whose OS is light, a variant no screenshot shows.
    expect(readStoredAppearance(workingStorage())).toBe('dark');
  });

  it('defaults to dark when the stored value is not a choice', () => {
    // Someone else's key, a truncated write, or a value from a build that had more options.
    expect(readStoredAppearance(workingStorage({ 'concordat.appearance': 'midnight' }))).toBe(
      'dark',
    );
  });

  it('defaults to dark when storage is unavailable', () => {
    expect(readStoredAppearance(null)).toBe('dark');
  });

  it('defaults to dark when reading storage throws', () => {
    // Safari in private browsing, or a third-party-cookie policy on an embedded page. An
    // unreadable preference is not a reason to fail to render — and this is called from
    // `main.ts` before bootstrap, so an exception here means a blank page, not a stack
    // trace in a component.
    expect(readStoredAppearance(blockedStorage())).toBe('dark');
  });
});

describe('storeAppearance', () => {
  it('persists the choice under a namespaced key', () => {
    // The key is shared with `main.ts`'s pre-bootstrap read. Renaming it on one side only
    // means every reload silently forgets the theme.
    const storage = workingStorage();
    storeAppearance(storage, 'dark');

    expect(storage.getItem('concordat.appearance')).toBe('dark');
  });

  it('does nothing when storage is unavailable', () => {
    expect(() => storeAppearance(null, 'dark')).not.toThrow();
  });

  it('does nothing when writing throws', () => {
    // A theme that does not survive a reload beats a toggle that throws on click.
    expect(() => storeAppearance(blockedStorage(), 'dark')).not.toThrow();
  });
});

describe('resolveAppearance', () => {
  it('follows the OS when the choice is system', () => {
    expect(resolveAppearance('system', true)).toBe('dark');
    expect(resolveAppearance('system', false)).toBe('light');
  });

  it('lets an explicit choice override the OS', () => {
    // `system` is a third state, not a synonym for the current OS setting. A user who chose
    // light keeps light when their OS flips at dusk.
    expect(resolveAppearance('light', true)).toBe('light');
    expect(resolveAppearance('dark', false)).toBe('dark');
  });
});

describe('systemPrefersDark', () => {
  it('is false when there is no window to ask', () => {
    // Server-side rendering, or a `DOCUMENT` with no `defaultView`. Light is the safer
    // guess because it matches the pre-bootstrap default.
    expect(systemPrefersDark(null)).toBe(false);
  });

  it('asks for the prefers-color-scheme query specifically', () => {
    let asked: string | null = null;
    const view = {
      matchMedia: (query: string) => {
        asked = query;
        return { matches: true } as MediaQueryList;
      },
    } as unknown as Window;

    expect(systemPrefersDark(view)).toBe(true);
    expect(asked).toBe('(prefers-color-scheme: dark)');
  });
});

describe('applyAppearance', () => {
  it('puts the class Spartan actually keys off on the root', () => {
    // Every dark-mode style in the app is `&:is(.dark *)`. Renaming this string unstyles
    // the entire dark theme without breaking a single type.
    const root = document.createElement('html');

    applyAppearance(root, 'dark');

    expect(root.classList.contains('dark')).toBe(true);
  });

  it('removes the class again rather than only ever adding it', () => {
    // The toggle has to work in both directions, and the class survives navigation because
    // it is on the document root.
    const root = document.createElement('html');
    root.classList.add('dark');

    applyAppearance(root, 'light');

    expect(root.classList.contains('dark')).toBe(false);
  });

  it('leaves unrelated classes on the root alone', () => {
    const root = document.createElement('html');
    root.classList.add('js-enabled');

    applyAppearance(root, 'dark');

    expect(root.classList.contains('js-enabled')).toBe(true);
  });
});

describe('applyStoredAppearance', () => {
  it('resolves and applies in one step, the way main.ts needs it to', () => {
    // `main.ts` calls this before `bootstrapApplication`, so whatever it does is what the
    // first paint shows. It must reach the same answer `ThemeStore` will reach a moment
    // later, or the page changes theme as Angular starts.
    const root = document.createElement('html');
    const stored = workingStorage({ 'concordat.appearance': 'dark' });
    const fake = {
      documentElement: root,
      defaultView: { localStorage: stored, matchMedia: () => ({ matches: false }) },
    } as unknown as Document;

    applyStoredAppearance(fake);

    expect(root.classList.contains('dark')).toBe(true);
  });

  it('falls back to the OS preference when nothing is stored', () => {
    const root = document.createElement('html');
    const fake = {
      documentElement: root,
      defaultView: { localStorage: workingStorage(), matchMedia: () => ({ matches: true }) },
    } as unknown as Document;

    applyStoredAppearance(fake);

    expect(root.classList.contains('dark')).toBe(true);
  });
});
