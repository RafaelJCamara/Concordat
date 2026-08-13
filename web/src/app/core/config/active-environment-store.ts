import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { CONCORDAT_CONFIG } from './app-config';

/**
 * The environment every registry read is scoped to.
 *
 * In `core/` rather than in a feature because environments are a first-class isolation
 * boundary (DESIGN §4) and *everything* hangs off one — the dashboard, the subject list,
 * contracts, audit. If each feature kept its own copy, switching environment would update
 * whichever screen is open and leave the rest showing another environment's data, labelled
 * as if it were this one. That is a wrong answer presented confidently, which is worse than
 * an error.
 */
export const ActiveEnvironmentStore = signalStore(
  { providedIn: 'root' },
  withState(() => ({ name: inject(CONCORDAT_CONFIG).defaultEnvironment })),
  withMethods((store) => ({
    /** Switches environment. Callers reload their own data; nothing is cached across the switch. */
    select(name: string): void {
      patchState(store, { name });
    },
  })),
);
