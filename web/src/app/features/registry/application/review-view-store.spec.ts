import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  DEFAULT_REVIEW_VIEW,
  REVIEW_VIEW_STORAGE_KEY,
  readStoredReviewView,
  storeReviewView,
  ReviewViewStore,
} from './review-view-store';

// The choice must survive a reload, and a browser that refuses storage must cost the screen
// nothing but the memory. Blocked storage throws from the property access itself, not only from
// `getItem`, so both are exercised.

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

const refuse = (): never => {
  throw new DOMException('The operation is insecure.', 'SecurityError');
};

describe('readStoredReviewView', () => {
  it('starts on the summary, which is the screen as it was', () => {
    expect(DEFAULT_REVIEW_VIEW).toBe('summary');
    expect(readStoredReviewView(() => workingStorage())).toBe('summary');
    expect(readStoredReviewView(() => null)).toBe('summary');
  });

  it.each(['detailed', 'summary'] as const)('returns a stored %s', (view) => {
    expect(readStoredReviewView(() => workingStorage({ [REVIEW_VIEW_STORAGE_KEY]: view }))).toBe(
      view,
    );
  });

  it.each(['', 'Detailed', 'diff', 'null', '{"view":"detailed"}'])(
    'ignores a stored value it does not know: %j',
    (value) => {
      expect(readStoredReviewView(() => workingStorage({ [REVIEW_VIEW_STORAGE_KEY]: value }))).toBe(
        'summary',
      );
    },
  );

  it('falls back when reading throws, and when merely getting the storage throws', () => {
    const blocked = { ...workingStorage(), getItem: refuse } as Storage;

    expect(readStoredReviewView(() => blocked)).toBe('summary');
    expect(readStoredReviewView(refuse)).toBe('summary');
  });
});

describe('storeReviewView', () => {
  it('writes under the key the reader reads', () => {
    const storage = workingStorage();

    storeReviewView(() => storage, 'detailed');

    expect(storage.getItem(REVIEW_VIEW_STORAGE_KEY)).toBe('detailed');
    expect(readStoredReviewView(() => storage)).toBe('detailed');
  });

  it('does nothing, and does not throw, when storage is blocked', () => {
    const blocked = { ...workingStorage(), setItem: refuse } as Storage;

    expect(() => storeReviewView(() => blocked, 'detailed')).not.toThrow();
    expect(() => storeReviewView(refuse, 'detailed')).not.toThrow();
  });
});

describe('ReviewViewStore', () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => localStorage.clear());

  it('starts on what was stored', () => {
    localStorage.setItem(REVIEW_VIEW_STORAGE_KEY, 'detailed');

    expect(TestBed.inject(ReviewViewStore).view()).toBe('detailed');
  });

  it('starts on the summary with nothing stored', () => {
    expect(TestBed.inject(ReviewViewStore).view()).toBe('summary');
  });

  it('remembers a choice for the next visit', () => {
    TestBed.inject(ReviewViewStore).choose('detailed');

    expect(localStorage.getItem(REVIEW_VIEW_STORAGE_KEY)).toBe('detailed');

    // A new visit: a new injector reads what the last one left.
    TestBed.resetTestingModule();

    expect(TestBed.inject(ReviewViewStore).view()).toBe('detailed');
  });

  it('keeps the choice for the session when storage is blocked', () => {
    const original = Object.getOwnPropertyDescriptor(window, 'localStorage');
    Object.defineProperty(window, 'localStorage', { configurable: true, get: refuse });

    try {
      const store = TestBed.inject(ReviewViewStore);

      expect(store.view()).toBe('summary');

      store.choose('detailed');

      expect(store.view()).toBe('detailed');
    } finally {
      if (original) {
        Object.defineProperty(window, 'localStorage', original);
      }
    }
  });
});
