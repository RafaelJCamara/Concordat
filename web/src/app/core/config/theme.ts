// Theme resolution, with no Angular in it.
//
// Split out from `ThemeStore` because `main.ts` has to apply the theme *before*
// `bootstrapApplication` runs. The body is empty until Angular renders, so the only thing
// that can flash is the page background — and it will, for anyone whose stored choice
// disagrees with their OS, unless the class is on `<html>` by the time the first paint
// happens. Two callers, one implementation, no chance of them disagreeing.

/** What the user asked for. `system` defers to the OS. */
export type Appearance = 'light' | 'dark' | 'system';

/** What that resolves to once the OS preference is known. */
export type ResolvedAppearance = 'light' | 'dark';

/** The class Spartan's `dark` variant keys off (`&:is(.dark *)`). */
const DARK_CLASS = 'dark';

const STORAGE_KEY = 'concordat.appearance';

const APPEARANCES: readonly Appearance[] = ['light', 'dark', 'system'];

/**
 * What a visitor gets before they have chosen anything.
 *
 * `dark`, not `system`. The design is a dark developer theme (ADR-006) — the prototype
 * shipped no light palette at all, and the teal-on-near-black is the product's face. The
 * light theme DESIGN §9 adds exists for the people who want it, and defaulting to
 * `system` would hand it instead to every first-time visitor on a light OS, which is most
 * of them. They would see a version of the interface nobody designed and no screenshot
 * shows. A deliberate choice still wins, and still survives a reload.
 */
const DEFAULT_APPEARANCE: Appearance = 'dark';

/**
 * The stored choice, defaulting to {@link DEFAULT_APPEARANCE}.
 *
 * Storage access is wrapped because it throws, not returns null, when the browser has
 * blocked it — Safari in private browsing, or a third-party-cookie policy on an embedded
 * page. An unreadable preference is not a reason to fail to render.
 */
export function readStoredAppearance(storage: Storage | null): Appearance {
  try {
    const stored = storage?.getItem(STORAGE_KEY);
    return isAppearance(stored) ? stored : DEFAULT_APPEARANCE;
  } catch {
    return DEFAULT_APPEARANCE;
  }
}

/** Persists the choice, silently doing nothing when storage is unavailable. */
export function storeAppearance(storage: Storage | null, appearance: Appearance): void {
  try {
    storage?.setItem(STORAGE_KEY, appearance);
  } catch {
    /* A theme that does not survive a reload beats a page that does not load. */
  }
}

/** Whether the OS is asking for a dark interface. */
export function systemPrefersDark(window: Window | null): boolean {
  return window?.matchMedia('(prefers-color-scheme: dark)').matches ?? false;
}

/** Collapses a choice and an OS preference into the theme actually in force. */
export function resolveAppearance(
  appearance: Appearance,
  prefersDark: boolean,
): ResolvedAppearance {
  if (appearance === 'system') {
    return prefersDark ? 'dark' : 'light';
  }

  return appearance;
}

/** Puts the resolved theme on the document root. */
export function applyAppearance(root: Element, resolved: ResolvedAppearance): void {
  root.classList.toggle(DARK_CLASS, resolved === 'dark');
}

/**
 * Applies the stored theme to a document. Call this before bootstrapping.
 */
export function applyStoredAppearance(document: Document): void {
  const view = document.defaultView;
  const appearance = readStoredAppearance(view?.localStorage ?? null);

  applyAppearance(document.documentElement, resolveAppearance(appearance, systemPrefersDark(view)));
}

function isAppearance(value: string | null | undefined): value is Appearance {
  return value !== null && value !== undefined && APPEARANCES.includes(value as Appearance);
}
