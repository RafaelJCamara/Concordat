import { computed, signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SessionStore } from '../../../core/auth/session-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { Scope } from '../../../domain/identity/scope';
import type { Subject } from '../../../domain/registry/subject';
import { SubjectListStore } from '../application/subject-list-store';
import { SubjectListPage } from './subject-list-page';

// The screen's own logic is the search box, and that is what this file is about. Loading,
// failing, retrying and deciding when to ask belong to `SubjectListStore` and are tested there;
// asserting them again through the page would be the same test with a slower setup.
//
// The box is the registry's prefix filter (ADR-038). What this file pins is therefore not what
// matches — the page must not decide that — but that the page *hands over what was typed*,
// shows what the store holds without filtering it again, and keeps the box on screen through every
// state a search can produce, which is the thing a screen that swapped the box into and out of
// its branches got wrong.

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
 * `isEmpty` and `noMatches` restate the store's own definitions so the page can be driven through
 * them; the store's spec is what holds those honest.
 */
function stubStore(subjects: readonly Subject[] = SUBJECTS) {
  const state = {
    subjects: signal<readonly Subject[]>(subjects),
    loading: signal(false),
    loaded: signal(true),
    error: signal<ConcordatError | null>(null),
    shown: signal(''),
    prefix: signal(''),
    searching: signal(false),
    loadingMore: signal(false),
    moreError: signal<ConcordatError | null>(null),
    /** What the registry says there are, when a test wants it to differ from the rows held. */
    totalOverride: signal<number | null>(null),
  };

  const total = computed(() => state.totalOverride() ?? state.subjects().length);

  return {
    ...state,
    total,
    unread: computed(() => Math.max(total() - state.subjects().length, 0)),
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
    load: vi.fn(),
    loadMore: vi.fn(),
    search: vi.fn(),
  };
}

type Stub = ReturnType<typeof stubStore>;

function render(store: Stub, scopes: readonly Scope[] = ['subject:write']) {
  TestBed.configureTestingModule({
    // The router is for `SubjectTable`'s links to the detail screen, not for this page.
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

  const fixture = TestBed.createComponent(SubjectListPage);
  fixture.detectChanges();

  return fixture;
}

describe('SubjectListPage: the search box', () => {
  let store: Stub;
  let fixture: ComponentFixture<SubjectListPage>;

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
    store = stubStore();
    fixture = render(store);
  });

  it('lists what the store holds, before anything is typed', () => {
    expect(rows()).toHaveLength(3);
    expect(store.search).not.toHaveBeenCalled();
  });

  it('hands what was typed to the store, untouched', () => {
    // The store trims it and the registry judges it. A page that trimmed or lower-cased it would
    // be deciding something about the search that is not its to decide, and lower-casing would be
    // wrong on its face: names are case-sensitive.
    type('  Acme.Orders. ');

    expect(store.search).toHaveBeenCalledExactlyOnceWith('  Acme.Orders. ');
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

  it('says how many matched, and for which prefix', () => {
    store.subjects.set([SUBJECTS[0] as Subject]);
    store.shown.set('acme.orders.OrderC');
    fixture.detectChanges();

    expect(host().textContent).toContain('1 subject starting with');
    expect(host().textContent).toContain('acme.orders.OrderC');
  });

  it('takes no more than the longest name, since nothing longer can match', () => {
    expect(box()?.getAttribute('maxlength')).toBe('512');
  });

  it('says what the box matches when nothing has been typed, so a person is not left to guess', () => {
    expect(host().textContent).toContain('Matches the start of a name, and the case you type.');
  });

  it('is absent before the first answer, and in an environment with no subjects at all', () => {
    TestBed.resetTestingModule();
    const first = stubStore([]);
    first.loaded.set(false);
    first.loading.set(true);
    fixture = render(first);
    expect(box()).toBeNull();

    TestBed.resetTestingModule();
    const empty = stubStore([]);
    fixture = render(empty);
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

    it('is cleared from the panel in one step, and asks the store for everything', () => {
      type('nothing.');
      store.subjects.set([]);
      store.shown.set('nothing.');
      fixture.detectChanges();
      store.search.mockClear();

      const clear = [...host().querySelectorAll('button')].find((b) =>
        b.textContent?.includes('Clear search'),
      );
      clear?.click();
      fixture.detectChanges();

      expect(store.search).toHaveBeenCalledExactlyOnceWith('');
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
    const fixture = render(stubStore(subjects), scopes);

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

describe('SubjectListPage: a list longer than the screen has read (ADR-045)', () => {
  const extent = (host: HTMLElement) =>
    host.querySelector('[data-testid="list-extent"]')?.textContent?.replace(/\s+/g, ' ') ?? null;
  const showMore = (host: HTMLElement) =>
    [...host.querySelectorAll('button')].find((b) => b.textContent?.trim().startsWith('Show more'));

  function open(store: Stub) {
    const fixture = render(store);

    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  it('says how much of the list it is showing, and offers the rest', () => {
    const store = stubStore();
    store.totalOverride.set(120);

    const { host } = open(store);

    expect(extent(host)).toContain('Showing the first 3 of 120');
    expect(extent(host)).toContain('117 more are not shown yet');
    expect(showMore(host)).toBeDefined();
  });

  it('says "is" for the one left', () => {
    const store = stubStore();
    store.totalOverride.set(4);

    expect(extent(open(store).host)).toContain('1 more is not shown yet');
  });

  it('asks the store for the next page when asked', () => {
    const store = stubStore();
    store.totalOverride.set(120);
    const { host } = open(store);

    showMore(host)!.click();

    expect(store.loadMore).toHaveBeenCalledTimes(1);
  });

  it('says nothing about extent when the whole list is on screen', () => {
    const { host } = open(stubStore());

    expect(extent(host)).toBeNull();
    expect(showMore(host)).toBeUndefined();
  });

  it('names the prefix the rows answer, not the one that was typed since', () => {
    const store = stubStore();
    store.totalOverride.set(120);
    store.shown.set('acme.orders.');
    store.prefix.set('something-else');

    expect(extent(open(store).host)).toContain('starting with acme.orders.');
  });

  it('counts the registry’s total under the box, not the rows it has read', () => {
    const store = stubStore();
    store.totalOverride.set(120);
    store.shown.set('acme.');

    expect(open(store).host.textContent).toContain('120 subjects starting with');
  });

  it('disables the button while a page is on its way', () => {
    const store = stubStore();
    store.totalOverride.set(120);
    store.loadingMore.set(true);

    const { host } = open(store);
    const button = [...host.querySelectorAll('button')].find(
      (b) => b.textContent?.trim() === 'Loading…',
    );

    expect(button).toBeDefined();
    expect(button?.disabled).toBe(true);
    expect(showMore(host)).toBeUndefined();
  });

  it('reports a page that could not be read, and keeps the rows that were', () => {
    const store = stubStore();
    store.totalOverride.set(120);
    store.moreError.set(new ConcordatError({ status: 500, code: 'x', detail: 'page two is down' }));

    const { host } = open(store);

    expect(host.textContent).toContain('Could not load more');
    expect(host.textContent).toContain('page two is down');
    expect(host.textContent).toContain('acme.orders.OrderCreated');
  });
});
