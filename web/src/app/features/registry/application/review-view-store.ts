import { DOCUMENT, inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';

/**
 * How a held change is shown when its review is opened (ADR-050).
 *
 * `detailed` is the schema as a line diff with the registry's findings on the lines they are
 * about; `summary` is the registry's list of findings on its own, which is what the screen showed
 * before the line diff existed.
 */
export const REVIEW_VIEWS = ['detailed', 'summary'] as const;

/** One of {@link REVIEW_VIEWS}. */
export type ReviewView = (typeof REVIEW_VIEWS)[number];

/**
 * What a reviewer gets before they have chosen.
 *
 * `summary`, because that is the screen as it was: a first visit changes nothing, and the line
 * diff costs two schema fetches per opened row, which should be something a reviewer asked for
 * once and not something every opened row did to them. The choice is remembered, so asking costs
 * one click for good.
 */
export const DEFAULT_REVIEW_VIEW: ReviewView = 'summary';

/** Where the choice is kept. The name says what it is for, and nothing else reads it. */
export const REVIEW_VIEW_STORAGE_KEY = 'concordat.approvals.review-view';

function isReviewView(value: unknown): value is ReviewView {
  return REVIEW_VIEWS.includes(value as ReviewView);
}

/**
 * The stored choice, or {@link DEFAULT_REVIEW_VIEW}.
 *
 * `storageOf` is a function and not a `Storage` because, in a browser that has blocked storage,
 * the *property access* `window.localStorage` is what throws — before there is anything to call
 * `getItem` on. Everything that can throw is inside the `try`. An unreadable preference is not a
 * reason to fail to render (the same rule as `readStoredAppearance`).
 */
export function readStoredReviewView(storageOf: () => Storage | null): ReviewView {
  try {
    const stored = storageOf()?.getItem(REVIEW_VIEW_STORAGE_KEY);
    return isReviewView(stored) ? stored : DEFAULT_REVIEW_VIEW;
  } catch {
    return DEFAULT_REVIEW_VIEW;
  }
}

/** Persists the choice, silently doing nothing when storage is unavailable. */
export function storeReviewView(storageOf: () => Storage | null, view: ReviewView): void {
  try {
    storageOf()?.setItem(REVIEW_VIEW_STORAGE_KEY, view);
  } catch {
    /* A choice that does not survive a reload beats a screen that does not open. */
  }
}

/**
 * The reviewer's choice between the line diff and the summary.
 *
 * <b>One value for the whole screen, not one per row.</b> It is a preference about how this
 * person reads a held change, so opening a second row shows it the way the first was, and the
 * radio group in every open row moves together. <b>It is kept in the browser and nowhere else</b>:
 * it is not an account setting and the registry has no field for it. Blocked storage costs the
 * memory and nothing more: the choice still holds until the tab closes.
 */
export const ReviewViewStore = signalStore(
  { providedIn: 'root' },
  withState(() => {
    const document = inject(DOCUMENT);

    return { view: readStoredReviewView(() => document.defaultView?.localStorage ?? null) };
  }),
  withMethods((store) => {
    const document = inject(DOCUMENT);

    return {
      /** Records a choice and persists it. */
      choose(view: ReviewView): void {
        patchState(store, { view });
        storeReviewView(() => document.defaultView?.localStorage ?? null, view);
      },
    };
  }),
);
