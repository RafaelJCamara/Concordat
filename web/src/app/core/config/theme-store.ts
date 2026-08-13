import { DOCUMENT, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import {
  patchState,
  signalStore,
  withComputed,
  withHooks,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import {
  applyAppearance,
  readStoredAppearance,
  resolveAppearance,
  storeAppearance,
  systemPrefersDark,
  type Appearance,
} from './theme';

/**
 * The light/dark choice, and the effect that puts it on the document.
 *
 * `system` is a real third state, not a synonym for whichever theme the OS happens to want
 * right now: a user who has chosen it expects the page to follow when they switch their OS
 * at dusk, without a reload. That is why the media query is watched rather than read once.
 */
export const ThemeStore = signalStore(
  { providedIn: 'root' },
  withState(() => ({
    appearance: readStoredAppearance(inject(DOCUMENT).defaultView?.localStorage ?? null),
  })),
  withProps(() => ({ prefersDark: watchPrefersDark() })),
  withComputed(({ appearance, prefersDark }) => ({
    resolved: computed(() => resolveAppearance(appearance(), prefersDark())),
  })),
  withMethods((store) => {
    const document = inject(DOCUMENT);

    return {
      /** Records a choice and persists it. */
      choose(appearance: Appearance): void {
        patchState(store, { appearance });
        storeAppearance(document.defaultView?.localStorage ?? null, appearance);
      },
    };
  }),
  withHooks({
    onInit(store) {
      const root = inject(DOCUMENT).documentElement;

      // `main.ts` has already applied the stored theme before bootstrap, so on a cold start
      // this effect writes the class that is already there. It exists for everything after:
      // the toggle, and the OS flipping under a `system` choice.
      effect(() => applyAppearance(root, store.resolved()));
    },
  }),
);

function watchPrefersDark() {
  const view = inject(DOCUMENT).defaultView;
  const prefersDark = signal(systemPrefersDark(view));

  const query = view?.matchMedia('(prefers-color-scheme: dark)');
  if (query) {
    const onChange = (event: MediaQueryListEvent) => prefersDark.set(event.matches);
    query.addEventListener('change', onChange);
    inject(DestroyRef).onDestroy(() => query.removeEventListener('change', onChange));
  }

  return prefersDark.asReadonly();
}
