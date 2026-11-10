import { computed, signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SessionStore } from '../../../core/auth/session-store';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { Scope } from '../../../domain/identity/scope';
import type { Subject } from '../../../domain/registry/subject';
import { DEFAULT_PAGE_SIZE, pageCount } from '../application/subject-list-query';
import { SEARCH_DEBOUNCE_MS, SubjectListStore } from '../application/subject-list-store';
import { SubjectListPage } from './subject-list-page';

// The screen's own logic: the search box, and the address (ADR-052). Loading, failing, retrying and
// deciding what to ask belong to `SubjectListStore` and are tested there; asserting them again through
// the page would be the same test with a slower setup.
//
// The box is the registry's prefix filter (ADR-038). What this file pins is therefore not what
// matches — the page must not decide that — but that the page puts what was typed into the address,
// shows what the store holds without filtering it again, and keeps the box on screen through every
// state a search can produce, which is the thing a screen that swapped the box into and out of its
// branches got wrong. And that a click is a change of address and never a call to the store: the store
// is told what to show only by the address, so a click, a reload and the Back button cannot disagree.

function subject(overrides: Partial<Subject> & { name: string }): Subject {
  return {
    format: 'json',
    owner: 'orders-team',
    reviewers: [],
    lifecycle: 'ACTIVE',
    contentModel: 'OPEN',
    compatibilityPolicy: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
    latest: null,
    versions: [],
    ...overrides,
  };
}

const SUBJECTS: readonly Subject[] = [
  subject({ name: 'acme.orders.OrderCreated', owner: 'orders-team' }),
  subject({ name: 'acme.orders.OrderShipped', owner: 'orders-team' }),
  subject({ name: 'acme.billing.InvoiceRaised', owner: 'billing-team' }),
];

/**
 * A `SubjectListStore`, stubbed, whose state the test moves.
 *
 * The store is replaced rather than the API underneath it, because the boundaries rule says a
 * routed component may not reach into `data-access` — and a test that imports `SubjectsApi` to
 * build a fake has just done, from the same folder, the thing the rule exists to prevent. The
 * store is this page's only collaborator, so it is also the only thing worth substituting.
 *
 * The computed values restate the store's own definitions so the page can be driven through them; the
 * store's spec is what holds those honest.
 */
function stubStore(subjects: readonly Subject[] = SUBJECTS) {
  const state = {
    subjects: signal<readonly Subject[]>(subjects),
    loading: signal(false),
    loaded: signal(true),
    error: signal<ConcordatError | null>(null),
    shown: signal(''),
    shownPage: signal(1),
    shownSize: signal(DEFAULT_PAGE_SIZE),
    prefix: signal(''),
    page: signal(1),
    size: signal(DEFAULT_PAGE_SIZE),
    searching: signal(false),
    outOfRange: signal<{ requested: number; last: number } | null>(null),
    clampedFrom: signal<number | null>(null),
    /** What the registry says there are, when a test wants it to differ from the rows held. */
    totalOverride: signal<number | null | undefined>(undefined),
  };

  const total = computed(() =>
    state.totalOverride() === undefined ? state.subjects().length : state.totalOverride()!,
  );

  return {
    ...state,
    total,
    pages: computed(() => {
      const n = total();

      return n === null ? null : pageCount(n, state.shownSize());
    }),
    firstRow: computed(() =>
      state.subjects().length === 0 ? 0 : (state.shownPage() - 1) * state.shownSize() + 1,
    ),
    lastRow: computed(() =>
      state.subjects().length === 0
        ? 0
        : (state.shownPage() - 1) * state.shownSize() + state.subjects().length,
    ),
    hasMore: computed(() => {
      const n = total();

      return n === null
        ? state.subjects().length >= state.shownSize()
        : (state.shownPage() - 1) * state.shownSize() + state.subjects().length < n;
    }),
    isEmpty: computed(
      () =>
        state.loaded() && !state.loading() && state.subjects().length === 0 && state.shown() === '',
    ),
    noMatches: computed(
      () =>
        state.loaded() &&
        state.error() === null &&
        state.subjects().length === 0 &&
        state.shown() !== '',
    ),
    show: vi.fn(),
    reload: vi.fn(),
    load: vi.fn(),
  };
}

type Stub = ReturnType<typeof stubStore>;

function render(
  store: Stub,
  scopes: readonly Scope[] = ['subject:write'],
  /** The address, as the router would have bound it before the first render. */
  address: Record<string, string> = {},
) {
  TestBed.configureTestingModule({
    // The router is for `SubjectTable`'s links to the detail screen, and for the page's own navigation,
    // which is spied on and goes nowhere.
    providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });

  // The page provides its own store, so a root-level provider would never be reached. This
  // replaces the component-level one, which is the only place it is registered.
  TestBed.overrideComponent(SubjectListPage, {
    set: {
      providers: [
        {
          provide: SubjectListStore,
          useValue: store as unknown as InstanceType<typeof SubjectListStore>,
        },
      ],
    },
  });
  TestBed.inject(SessionStore).observeInstance({ claimed: true, actor: 'someone', scopes });

  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const fixture = TestBed.createComponent(SubjectListPage);

  for (const [name, value] of Object.entries(address)) {
    fixture.componentRef.setInput(name, value);
  }

  fixture.detectChanges();

  return { fixture, navigate };
}

/** The address the last navigation asked for: the three parameters and whether it replaced the entry. */
function lastNavigation(navigate: ReturnType<typeof vi.spyOn>) {
  const call = navigate.mock.calls.at(-1) as unknown as [
    unknown[],
    {
      queryParams: Record<string, string | null>;
      replaceUrl: boolean;
      queryParamsHandling: string;
    },
  ];

  return {
    ...call[1].queryParams,
    replace: call[1].replaceUrl,
    handling: call[1].queryParamsHandling,
  };
}

describe('SubjectListPage: the search box', () => {
  let store: Stub;
  let fixture: ComponentFixture<SubjectListPage>;
  let navigate: ReturnType<typeof vi.spyOn>;

  const host = () => fixture.nativeElement as HTMLElement;
  const box = () => host().querySelector<HTMLInputElement>('input[type=search]');
  const rows = () => [...host().querySelectorAll('tbody tr')].map((row) => row.textContent ?? '');

  /** Types into the box the way a person does, rather than poking a signal. */
  function type(text: string) {
    const input = box() as HTMLInputElement;

    input.value = text;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  beforeEach(() => {
    vi.useFakeTimers();
    store = stubStore();
    ({ fixture, navigate } = render(store));
    navigate.mockClear();
  });

  afterEach(() => vi.useRealTimers());

  it('lists what the store holds, before anything is typed', () => {
    expect(rows()).toHaveLength(3);
    expect(navigate).not.toHaveBeenCalled();
  });

  it('puts what was typed into the address once typing pauses, trimmed as the registry will, and not before', () => {
    type('  Acme.Orders. ');

    expect(navigate).not.toHaveBeenCalled();
    vi.advanceTimersByTime(SEARCH_DEBOUNCE_MS - 1);
    expect(navigate).not.toHaveBeenCalled();

    vi.advanceTimersByTime(1);

    // Trimmed, and the case kept: names are case-sensitive. The box itself is not reformatted.
    expect(navigate).toHaveBeenCalledTimes(1);
    expect(lastNavigation(navigate)).toMatchObject({ q: 'Acme.Orders.', page: null });
    expect(box()?.value).toBe('  Acme.Orders. ');
  });

  it('changes the address once for a burst of typing, to what was typed last', () => {
    type('a');
    vi.advanceTimersByTime(100);
    type('ac');
    vi.advanceTimersByTime(100);
    type('acme.');
    vi.advanceTimersByTime(SEARCH_DEBOUNCE_MS);

    expect(navigate).toHaveBeenCalledTimes(1);
    expect(lastNavigation(navigate)).toMatchObject({ q: 'acme.' });
  });

  it('replaces the history entry for a search and does not add one per prefix', () => {
    type('acme.');
    vi.advanceTimersByTime(SEARCH_DEBOUNCE_MS);

    expect(lastNavigation(navigate).replace).toBe(true);
  });

  it('goes back to the first page when the prefix changes, keeping the size', () => {
    fixture.componentRef.setInput('page', '4');
    fixture.componentRef.setInput('size', '25');
    fixture.detectChanges();
    navigate.mockClear();

    type('acme.');
    vi.advanceTimersByTime(SEARCH_DEBOUNCE_MS);

    expect(lastNavigation(navigate)).toMatchObject({ q: 'acme.', page: null, size: '25' });
  });

  it('keeps the other parameters in the address it is not changing', () => {
    type('acme.');
    vi.advanceTimersByTime(SEARCH_DEBOUNCE_MS);

    expect(lastNavigation(navigate).handling).toBe('merge');
  });

  it('does not change the address for text that trims to the prefix already there', () => {
    fixture.componentRef.setInput('q', 'acme.');
    fixture.detectChanges();
    navigate.mockClear();

    type(' acme. ');
    vi.advanceTimersByTime(SEARCH_DEBOUNCE_MS);

    expect(navigate).not.toHaveBeenCalled();
  });

  it('does not filter the rows itself', () => {
    // It used to: a substring match over the list in memory. Now the registry answers, and the
    // page shows whatever the store was given — so a prefix that matches none of the rows on
    // screen leaves them alone until an answer arrives, rather than racing it.
    type('zzz');

    expect(rows()).toHaveLength(3);
  });

  it('shows the rows the store holds once an answer replaces them', () => {
    type('acme.orders.');
    store.subjects.set([SUBJECTS[0] as Subject, SUBJECTS[1] as Subject]);
    store.shown.set('acme.orders.');
    fixture.detectChanges();

    expect(rows()).toHaveLength(2);
    expect(rows()[0]).toContain('acme.orders.OrderCreated');
  });

  it('takes no more than the longest name, since nothing longer can match', () => {
    expect(box()?.getAttribute('maxlength')).toBe('512');
  });

  it('says what the box matches, so a person is not left to guess', () => {
    expect(host().textContent).toContain('Matches the start of a name, and the case you type.');
  });

  describe('following the address', () => {
    it('shows the prefix the address names, in the box, when it changes (Back, a pasted link)', () => {
      fixture.componentRef.setInput('q', 'acme.billing.');
      fixture.detectChanges();

      expect(box()?.value).toBe('acme.billing.');
    });

    it('empties the box when the address no longer names a prefix', () => {
      fixture.componentRef.setInput('q', 'acme.');
      fixture.detectChanges();
      fixture.componentRef.setInput('q', undefined);
      fixture.detectChanges();

      expect(box()?.value).toBe('');
    });

    it('does not rewrite the box under the person while they type something that trims to the same prefix', () => {
      fixture.componentRef.setInput('q', 'acme.');
      fixture.detectChanges();

      type('acme. ');
      fixture.componentRef.setInput('size', '25');
      fixture.detectChanges();

      expect(box()?.value).toBe('acme. ');
    });
  });

  it('is absent before the first answer, and in an environment with no subjects at all', () => {
    TestBed.resetTestingModule();
    const first = stubStore([]);
    first.loaded.set(false);
    first.loading.set(true);
    ({ fixture } = render(first));
    expect(box()).toBeNull();

    TestBed.resetTestingModule();
    const empty = stubStore([]);
    ({ fixture } = render(empty));
    expect(box()).toBeNull();
    expect(host().textContent).toContain('No subjects yet');
  });

  describe('through every state a search produces', () => {
    it('stays on screen, and is the same element, while the next answer is awaited', () => {
      // The defect this pins: a box that lived in the branch that is replaced by a skeleton is
      // destroyed when a search starts, and takes the focus and the cursor with it, so typing a
      // second character goes nowhere.
      const before = box();
      type('a');

      store.loading.set(true);
      store.searching.set(true);
      fixture.detectChanges();

      expect(box()).toBe(before);
      expect(host().querySelector('[aria-busy="true"]')).not.toBeNull();
      // The rows it is replacing are still there, dimmed, rather than a skeleton in their place.
      expect(rows()).toHaveLength(3);
      expect(host().querySelector('[hlmskeleton]')).toBeNull();
    });

    it('stays, and is the same element, when the answer is rows', () => {
      const before = box();
      type('acme.');
      store.loading.set(true);
      store.searching.set(true);
      fixture.detectChanges();

      store.subjects.set([SUBJECTS[2] as Subject]);
      store.shown.set('acme.');
      store.loading.set(false);
      store.searching.set(false);
      fixture.detectChanges();

      expect(box()).toBe(before);
      expect(box()?.value).toBe('acme.');
      expect(rows()).toHaveLength(1);
    });

    it('stays when the answer is nothing, and says what was looked for', () => {
      const before = box();
      type('nothing.');
      store.subjects.set([]);
      store.shown.set('nothing.');
      fixture.detectChanges();

      expect(box()).toBe(before);
      expect(rows()).toHaveLength(0);
      expect(host().textContent).toContain('No subject names start with');
      expect(host().textContent).toContain('“nothing.”');
      // Not the empty-environment state, which would offer to create the first subject and
      // take the box away from somebody who needs it to type something else.
      expect(host().textContent).not.toContain('No subjects yet');
      expect(host().textContent).not.toContain('Create the first subject');
    });

    it('says why, when the prefix could never match a name', () => {
      type('acme-');
      store.subjects.set([]);
      store.shown.set('acme-');
      fixture.detectChanges();

      expect(host().textContent).toContain("'-' (U+002D) is not allowed in a subject name");
    });

    it('explains case and what is matched, when the prefix could have matched', () => {
      type('order');
      store.subjects.set([]);
      store.shown.set('order');
      fixture.detectChanges();

      const text = host().textContent ?? '';
      expect(text).toContain('Names are case-sensitive');
      expect(text).toContain('not the owner, and not the middle');
      expect(text).not.toContain('is not allowed in a subject name');
    });

    it('is cleared from the panel in one step, with no pause, and the address loses its prefix', () => {
      fixture.componentRef.setInput('q', 'nothing.');
      fixture.detectChanges();
      store.subjects.set([]);
      store.shown.set('nothing.');
      fixture.detectChanges();
      navigate.mockClear();

      const clear = [...host().querySelectorAll('button')].find((b) =>
        b.textContent?.includes('Clear search'),
      );
      clear?.click();
      fixture.detectChanges();

      expect(navigate).toHaveBeenCalledTimes(1);
      expect(lastNavigation(navigate)).toMatchObject({ q: null, page: null });
      expect(box()?.value).toBe('');
    });

    it('is not put back by a pause that was still running when it was cleared', () => {
      fixture.componentRef.setInput('q', 'nothing.');
      fixture.detectChanges();
      type('nothing.at');
      store.subjects.set([]);
      store.shown.set('nothing.');
      fixture.detectChanges();
      navigate.mockClear();

      [...host().querySelectorAll('button')]
        .find((b) => b.textContent?.includes('Clear search'))
        ?.click();
      fixture.componentRef.setInput('q', undefined);
      fixture.detectChanges();
      navigate.mockClear();
      vi.advanceTimersByTime(SEARCH_DEBOUNCE_MS * 2);

      expect(navigate).not.toHaveBeenCalled();
      expect(box()?.value).toBe('');
    });

    it('keeps showing the panel, rather than an empty table, while the next answer to a prefix that is still finding nothing is awaited', () => {
      type('nothing.');
      store.subjects.set([]);
      store.shown.set('nothing.');
      fixture.detectChanges();

      type('nothing.at');
      store.loading.set(true);
      store.searching.set(true);
      fixture.detectChanges();

      expect(host().textContent).toContain('No subject names start with');
      expect(host().querySelectorAll('tbody tr')).toHaveLength(0);
    });

    it('stays beside a failed search, so the person can change it', () => {
      type('acme.');
      store.subjects.set([]);
      store.shown.set('acme.');
      store.error.set(
        new ConcordatError({
          status: 0,
          code: 'registry_refused',
          detail: 'The registry is down.',
        }),
      );
      fixture.detectChanges();

      expect(host().textContent).toContain('Could not load subjects');
      expect(box()).not.toBeNull();
      // And a failure is not worded as a prefix that matched nothing.
      expect(host().textContent).not.toContain('No subject names start with');
    });

    it('retries through the store when asked to', () => {
      store.error.set(new ConcordatError({ status: 500, code: 'x', detail: 'down' }));
      fixture.detectChanges();

      [...host().querySelectorAll('button')]
        .find((b) => b.textContent?.includes('Try again'))
        ?.click();

      expect(store.reload).toHaveBeenCalledTimes(1);
    });

    it('shows a skeleton for a plain load, which has no rows to keep', () => {
      store.loading.set(true);
      store.searching.set(false);
      fixture.detectChanges();

      expect(host().querySelector('[aria-busy="true"]')).not.toBeNull();
      expect(rows()).toHaveLength(0);
    });
  });
});

// "New subject" is the only way into the create screen from here, and it used to be a button with
// no handler: a writer could press it and nothing happened. It is a link now, absent — not
// disabled — for a caller who cannot write (ADR-018), and the empty list carries the same step.
describe('SubjectListPage: creating a subject', () => {
  function renderAs(scopes: readonly Scope[], subjects: readonly Subject[]): HTMLElement {
    const { fixture } = render(stubStore(subjects), scopes);

    return fixture.nativeElement as HTMLElement;
  }

  const links = (host: HTMLElement) =>
    [...host.querySelectorAll('a[href="/subjects/new-subject"]')].map((a) => a.textContent?.trim());

  it('links "New subject" to the create screen for someone who can write', () => {
    expect(links(renderAs(['subject:write'], SUBJECTS))).toEqual(['New subject']);
  });

  it('offers it to an admin as well, who holds the other write scope', () => {
    expect(links(renderAs(['subject:admin'], SUBJECTS))).toEqual(['New subject']);
  });

  it('leaves it out, rather than disabling it, for a reader', () => {
    const host = renderAs(['subject:read'], SUBJECTS);

    expect(links(host)).toEqual([]);
    expect(host.textContent).not.toContain('New subject');
  });

  it('is a link and not a button that does nothing', () => {
    const host = renderAs(['subject:write'], SUBJECTS);

    expect(
      [...host.querySelectorAll('button')].some((b) => b.textContent?.includes('New subject')),
    ).toBe(false);
  });

  it('puts the next step beside an empty list, for a writer', () => {
    const host = renderAs(['subject:write'], []);

    expect(host.textContent).toContain('No subjects yet');
    expect(links(host)).toEqual(['New subject', 'Create the first subject']);
  });

  it('says the list is empty and offers nothing a reader cannot do', () => {
    const host = renderAs(['subject:read'], []);

    expect(host.textContent).toContain('No subjects yet');
    expect(links(host)).toEqual([]);
  });
});

// The address is the screen's state (ADR-052): what it says reaches the store, and every control writes it.
describe('SubjectListPage: the address and the pages', () => {
  let store: Stub;
  let fixture: ComponentFixture<SubjectListPage>;
  let navigate: ReturnType<typeof vi.spyOn>;

  const host = () => fixture.nativeElement as HTMLElement;
  const text = (selector: string) =>
    host().querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim() ?? null;
  const extent = () => text('[data-testid="list-extent"]');
  const pager = () => host().querySelector('cd-pagination');
  const pagerButton = (label: string) =>
    [...(pager()?.querySelectorAll('button') ?? [])].find(
      (b) => b.getAttribute('aria-label') === label || b.textContent?.trim() === label,
    );
  const sizeSelect = () => host().querySelector<HTMLSelectElement>('[data-testid="page-size"]')!;

  /** A page of the list as the store would hold it after the registry answered. */
  function onPage(page: number, total: number | null, size = DEFAULT_PAGE_SIZE) {
    const first = (page - 1) * size;
    const count = total === null ? size : Math.min(size, Math.max(total - first, 0));

    store.subjects.set(
      Array.from({ length: count }, (_, i) => subject({ name: `s${first + i + 1}` })),
    );
    store.totalOverride.set(total);
    store.page.set(page);
    store.shownPage.set(page);
    store.size.set(size);
    store.shownSize.set(size);
    fixture.detectChanges();
  }

  function setUp(initial: Record<string, string> = {}) {
    store = stubStore([]);
    ({ fixture, navigate } = render(store, ['subject:write'], initial));
  }

  describe('reading the address', () => {
    it('shows the first page of everything for a plain address, and starts from a clean slate', () => {
      setUp();

      expect(store.show).toHaveBeenCalledExactlyOnceWith(
        { prefix: '', page: 1, size: 50 },
        { fresh: false },
      );
    });

    it('hands the store the prefix, page and size the address names', () => {
      setUp({ q: 'acme.', page: '3', size: '25' });

      expect(store.show).toHaveBeenLastCalledWith(
        { prefix: 'acme.', page: 3, size: 25 },
        { fresh: false },
      );
    });

    it('shows another page when the address changes, as Back and Forward change it', () => {
      setUp({ page: '3' });
      store.show.mockClear();

      fixture.componentRef.setInput('page', '2');
      fixture.detectChanges();

      expect(store.show).toHaveBeenCalledExactlyOnceWith(
        { prefix: '', page: 2, size: 50 },
        { fresh: false },
      );
    });

    it('asks for nothing twice for an address that did not change', () => {
      setUp({ page: '3' });
      store.show.mockClear();

      fixture.componentRef.setInput('page', '3');
      fixture.detectChanges();

      expect(store.show).not.toHaveBeenCalled();
    });

    it.each([
      ['a page that is not a number', { page: 'abc' }, { page: null }],
      ['a page of zero', { page: '0' }, { page: null }],
      ['a page 1 spelled out', { page: '1' }, { page: null }],
      ['a size nobody offered', { size: '7' }, { size: null }],
      ['a prefix with spaces round it', { q: ' acme. ' }, { q: 'acme.' }],
    ])(
      'rewrites %s in place, and asks for nothing until it is rewritten',
      (_name, given, wanted) => {
        setUp(given);

        expect(store.show).not.toHaveBeenCalled();
        expect(lastNavigation(navigate)).toMatchObject({ ...wanted, replace: true });
      },
    );

    it('reads the first of a parameter the address repeated', () => {
      setUp();
      store.show.mockClear();

      fixture.componentRef.setInput('page', ['2', '9']);
      fixture.detectChanges();

      expect(store.show).toHaveBeenLastCalledWith(
        { prefix: '', page: 2, size: 50 },
        { fresh: false },
      );
    });
  });

  describe('changing environment', () => {
    it('starts again from a clean slate on the same page when it is the first', () => {
      setUp();
      store.show.mockClear();

      TestBed.inject(ActiveEnvironmentStore).select('prod');
      fixture.detectChanges();

      expect(store.show).toHaveBeenCalledExactlyOnceWith(
        { prefix: '', page: 1, size: 50 },
        { fresh: true },
      );
    });

    it('goes to the first page, in place of the address, when it was another: a page number belongs to its environment', () => {
      setUp({ page: '7', q: 'acme.' });
      store.show.mockClear();
      navigate.mockClear();

      TestBed.inject(ActiveEnvironmentStore).select('prod');
      fixture.detectChanges();

      expect(store.show).not.toHaveBeenCalled();
      expect(lastNavigation(navigate)).toMatchObject({ page: null, q: 'acme.', replace: true });

      // The address that results reaches the store as a fresh load, once.
      fixture.componentRef.setInput('page', undefined);
      fixture.detectChanges();

      expect(store.show).toHaveBeenCalledExactlyOnceWith(
        { prefix: 'acme.', page: 1, size: 50 },
        { fresh: true },
      );
    });

    it('does not mistake the first look for a switch', () => {
      setUp({ page: '7' });

      expect(navigate).not.toHaveBeenCalled();
      expect(store.show).toHaveBeenCalledTimes(1);
    });
  });

  describe('a page that is past the end', () => {
    it('goes to the last page in place of the address, and does not draw a table in between', () => {
      setUp({ page: '99' });
      navigate.mockClear();

      store.outOfRange.set({ requested: 99, last: 12 });
      fixture.detectChanges();

      expect(lastNavigation(navigate)).toMatchObject({ page: '12', replace: true });
    });

    it('says the page does not exist and which one this is', () => {
      setUp({ page: '12' });
      onPage(12, 600);
      store.clampedFrom.set(99);
      fixture.detectChanges();

      expect(text('[data-testid="page-clamped"]')).toBe(
        'Page 99 does not exist, so this is page 12, the last.',
      );
    });

    it('says nothing otherwise', () => {
      setUp();
      onPage(1, 600);

      expect(host().querySelector('[data-testid="page-clamped"]')).toBeNull();
    });
  });

  describe('saying where the rows are', () => {
    it('names the page, the rows and the whole length, and the order', () => {
      setUp({ page: '3' });
      onPage(3, 5000);

      expect(extent()).toBe('Page 3 of 100 · subjects 101–150 of 5,000 · newest first');
    });

    it('is a polite status, which is what a page change announces', () => {
      setUp();
      onPage(1, 130);

      const status = host().querySelector('[data-testid="list-extent"]');

      expect(status?.getAttribute('role')).toBe('status');
      expect(status?.getAttribute('aria-live')).toBe('polite');
    });

    it('names a prefix the rows answer', () => {
      setUp();
      onPage(1, 130);
      store.shown.set('acme.orders.');
      fixture.detectChanges();

      expect(extent()).toContain('starting with acme.orders.');
    });

    it('names the last page by the rows it holds', () => {
      setUp({ page: '3' });
      onPage(3, 107);

      expect(extent()).toBe('Page 3 of 3 · subjects 101–107 of 107 · newest first');
    });

    it('says the list goes on when the registry gave no count, and does not make one up', () => {
      setUp();
      onPage(1, null);

      expect(extent()).toBe('Page 1 · subjects 1–50 · more may follow · newest first');
      expect(extent()).not.toMatch(/\bof \d/);
    });

    it('says it is the end when there was no count and the page was not full', () => {
      setUp({ page: '2' });
      store.subjects.set([subject({ name: 's51' })]);
      store.totalOverride.set(null);
      store.shownPage.set(2);
      store.page.set(2);
      fixture.detectChanges();

      expect(extent()).toBe('Page 2 · subjects 51–51 · the end of the list · newest first');
    });

    it('does not name the order in the pager, but in the sentence and the table', () => {
      setUp();
      onPage(1, 130);

      expect(host().querySelector('caption')?.textContent).toContain('Newest first');
      expect(extent()).toContain('newest first');
    });
  });

  describe('the pager', () => {
    it('is there for a list of more than one page, as a labelled landmark', () => {
      setUp();
      onPage(1, 130);

      expect(pager()).not.toBeNull();
      expect(pager()?.querySelector('nav')?.getAttribute('aria-label')).toBe('Subject pages');
    });

    it('is not there for a list that fits one page: there is nothing to turn to', () => {
      setUp();
      onPage(1, 30);

      expect(pager()).toBeNull();
    });

    it('is there with no count when the page was full, and when it is not the first', () => {
      setUp();
      onPage(1, null);
      expect(pager()).not.toBeNull();

      store.subjects.set([subject({ name: 's51' })]);
      store.page.set(2);
      store.shownPage.set(2);
      fixture.detectChanges();
      expect(pager()).not.toBeNull();
    });

    it('marks the page the address asks for', () => {
      setUp({ page: '3' });
      onPage(3, 5000);

      expect(pager()?.querySelector('[aria-current="page"]')?.textContent?.trim()).toBe('3');
    });

    it('writes the page it is clicked on into the address, and adds a history entry', () => {
      setUp({ page: '3' });
      onPage(3, 5000);
      navigate.mockClear();

      pagerButton('Page 5')?.click();

      expect(navigate).toHaveBeenCalledTimes(1);
      expect(lastNavigation(navigate)).toMatchObject({ page: '5', q: null, replace: false });
    });

    it('writes the next and the previous page', () => {
      setUp({ page: '3' });
      onPage(3, 5000);
      navigate.mockClear();

      pagerButton('Next')?.click();
      expect(lastNavigation(navigate)).toMatchObject({ page: '4' });

      pagerButton('Previous')?.click();
      expect(lastNavigation(navigate)).toMatchObject({ page: '2' });
    });

    it('goes back to the plain address on the first page, which has no page parameter', () => {
      setUp({ page: '2' });
      onPage(2, 5000);
      navigate.mockClear();

      pagerButton('Page 1')?.click();

      expect(lastNavigation(navigate)).toMatchObject({ page: null });
    });

    it('keeps the prefix and the size when it turns the page', () => {
      setUp({ q: 'acme.', page: '2', size: '25' });
      onPage(2, 500, 25);
      navigate.mockClear();

      pagerButton('Next')?.click();

      expect(lastNavigation(navigate)).toMatchObject({ q: 'acme.', page: '3', size: '25' });
    });

    it('never calls the store itself: a click is a change of address', () => {
      setUp({ page: '3' });
      onPage(3, 5000);
      store.show.mockClear();

      pagerButton('Next')?.click();

      expect(store.show).not.toHaveBeenCalled();
    });
  });

  describe('the size of a page', () => {
    it('offers 25, 50 and 100, with the one asked for chosen', () => {
      setUp({ size: '25' });
      onPage(1, 130, 25);

      expect([...sizeSelect().options].map((o) => o.value)).toEqual(['25', '50', '100']);
      expect(sizeSelect().selectedOptions[0]?.value).toBe('25');
    });

    it('is labelled', () => {
      setUp();
      onPage(1, 130);

      expect(host().querySelector('label')?.textContent).toContain('Subjects per page');
    });

    function choose(value: string) {
      sizeSelect().value = value;
      sizeSelect().dispatchEvent(new Event('change'));
    }

    it('writes the size into the address and starts at the row that was being looked at', () => {
      // Page 3 of 25 begins at row 51: with 100 to a page that row is on the first.
      setUp({ page: '3', size: '25' });
      onPage(3, 500, 25);
      navigate.mockClear();

      choose('100');

      expect(lastNavigation(navigate)).toMatchObject({ size: '100', page: null, replace: false });
    });

    it('keeps a later page when the rows that were being looked at are on one', () => {
      setUp({ page: '5', size: '25' });
      onPage(5, 500, 25);
      navigate.mockClear();

      choose('50');

      expect(lastNavigation(navigate)).toMatchObject({ size: null, page: '3' });
    });

    it('ignores a size that is not offered, and the one already chosen', () => {
      setUp();
      onPage(1, 130);
      navigate.mockClear();

      choose('50');

      expect(navigate).not.toHaveBeenCalled();
    });
  });

  it('keeps the table and the pager for a page that is being replaced, dimmed', () => {
    setUp({ page: '2' });
    onPage(2, 500);

    store.loading.set(true);
    store.searching.set(true);
    fixture.detectChanges();

    expect(host().querySelectorAll('tbody tr')).toHaveLength(50);
    expect(host().querySelector('[aria-busy="true"]')).not.toBeNull();
    expect(pager()).not.toBeNull();
  });

  it('has no "Show more": a page is turned, not appended (ADR-052)', () => {
    setUp();
    onPage(1, 500);

    expect(host().textContent).not.toContain('Show more');
  });
});
