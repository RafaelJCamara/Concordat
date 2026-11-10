import { TestBed } from '@angular/core/testing';
import { Subject as ResponseStream, throwError } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { Subject, SubjectPage } from '../../../domain/registry/subject';
import { SubjectsApi } from '../data-access/subjects-api';
import { DEFAULT_PAGE_SIZE, type SubjectListQuery } from './subject-list-query';
import { SubjectListStore } from './subject-list-store';

// The store is told what to show and asks the registry for it (ADR-052). What is pinned here is what
// a screen cannot check for itself: which of two overlapping questions is still wanted, what the
// numbers on screen are the numbers of, and that a page past the end is a redirect and not a blank.
// The address, the debounce and the clicks belong to the page and are tested there.

function subject(name: string): Subject {
  return {
    name,
    format: 'json',
    owner: 'orders-team',
    reviewers: [],
    lifecycle: 'ACTIVE',
    contentModel: 'OPEN',
    compatibilityPolicy: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
    latest: null,
    versions: [],
  };
}

/** `count` subjects named `s<first>` .. , to fill a page without writing them out. */
function subjects(count: number, first = 1): Subject[] {
  return Array.from({ length: count }, (_, i) => subject(`s${first + i}`));
}

interface Request {
  readonly environment: string;
  readonly prefix: string | undefined;
  readonly limit: number | undefined;
  readonly offset: number | undefined;
  readonly sort: string | undefined;
}

/** A stand-in for `SubjectsApi` whose responses this file decides when to deliver. */
function fakeApi() {
  const streams: ResponseStream<SubjectPage>[] = [];
  const requests: Request[] = [];

  return {
    requests,
    streams,
    /** Answers the request at `index` (default the latest) with a page and the registry's total. */
    answer(items: readonly Subject[], total: number | null, index = streams.length - 1) {
      streams[index]!.next({ items, total });
    },
    fail(error: unknown, index = streams.length - 1) {
      streams[index]!.error(error);
    },
    listSubjects(
      environment: string,
      options: {
        prefix?: string;
        limit?: number;
        offset?: number;
        sort?: string;
      } = {},
    ) {
      requests.push({
        environment,
        prefix: options.prefix,
        limit: options.limit,
        offset: options.offset,
        sort: options.sort,
      });
      const stream = new ResponseStream<SubjectPage>();
      streams.push(stream);

      return stream.asObservable();
    },
  };
}

const FIRST: SubjectListQuery = { prefix: '', page: 1, size: DEFAULT_PAGE_SIZE };

describe('SubjectListStore', () => {
  let api: ReturnType<typeof fakeApi>;
  let store: InstanceType<typeof SubjectListStore>;
  let environments: InstanceType<typeof ActiveEnvironmentStore>;

  beforeEach(() => {
    api = fakeApi();
    TestBed.configureTestingModule({
      providers: [
        provideConcordatConfig({ defaultEnvironment: 'dev' }),
        { provide: SubjectsApi, useValue: api },
        SubjectListStore,
      ],
    });

    store = TestBed.inject(SubjectListStore);
    environments = TestBed.inject(ActiveEnvironmentStore);
  });

  it('starts idle rather than loading', () => {
    // A store that started `loading: true` would make every screen render a skeleton before
    // anything had asked for anything.
    expect(store.loading()).toBe(false);
    expect(store.loaded()).toBe(false);
    expect(store.error()).toBeNull();
    expect(store.subjects()).toEqual([]);
  });

  describe('what it asks the registry for', () => {
    it('asks for the newest first, one page of the size, at the offset the page is at (ADR-049, ADR-045)', () => {
      store.show({ prefix: 'acme.', page: 3, size: 25 });

      expect(api.requests).toEqual([
        { environment: 'dev', prefix: 'acme.', limit: 25, offset: 50, sort: 'newest' },
      ]);
    });

    it('starts at offset zero on the first page', () => {
      store.show(FIRST);

      expect(api.requests[0]).toMatchObject({ limit: 50, offset: 0, sort: 'newest' });
    });

    it('asks of the active environment', () => {
      environments.select('prod');

      store.show(FIRST);

      expect(api.requests[0]?.environment).toBe('prod');
    });

    it('asks again, from the first page of everything, for a screen that only wants to know a name is there', () => {
      store.load();

      expect(api.requests).toEqual([
        { environment: 'dev', prefix: '', limit: 50, offset: 0, sort: 'newest' },
      ]);
    });
  });

  describe('isEmpty', () => {
    it('is false before anything has been asked for', () => {
      // "No subjects" and "nobody has looked yet" are different screens.
      expect(store.isEmpty()).toBe(false);
    });

    it('is false while a load is in flight', () => {
      // The flicker this computed prevents: an empty list plus a pending request renders "no
      // subjects in this environment" for as long as the request takes.
      store.show(FIRST);

      expect(store.loading()).toBe(true);
      expect(store.isEmpty()).toBe(false);
    });

    it('is true once a load has finished and returned nothing', () => {
      store.show(FIRST);
      api.answer([], 0);

      expect(store.isEmpty()).toBe(true);
    });

    it('is false when the load returned rows', () => {
      store.show(FIRST);
      api.answer([subject('orders.created')], 1);

      expect(store.isEmpty()).toBe(false);
    });

    it('is not what an empty answer to a prefix is', () => {
      store.show({ ...FIRST, prefix: 'zzz' });
      api.answer([], 0);

      expect(store.isEmpty()).toBe(false);
      expect(store.noMatches()).toBe(true);
    });
  });

  describe('the answer', () => {
    it('publishes the page, the total, and what it is the answer to', () => {
      store.show({ prefix: 'acme.', page: 2, size: 25 });
      api.answer(subjects(25, 26), 130);

      expect(store.subjects()).toHaveLength(25);
      expect(store.total()).toBe(130);
      expect(store.shown()).toBe('acme.');
      expect(store.shownPage()).toBe(2);
      expect(store.shownSize()).toBe(25);
      expect(store.loading()).toBe(false);
      expect(store.loaded()).toBe(true);
      expect(store.error()).toBeNull();
    });

    it('numbers the rows by their place in the whole list', () => {
      store.show({ ...FIRST, page: 3 });
      api.answer(subjects(50, 101), 5000);

      expect(store.firstRow()).toBe(101);
      expect(store.lastRow()).toBe(150);
    });

    it('counts a last page that is not full by the rows it holds', () => {
      store.show({ ...FIRST, page: 3 });
      api.answer(subjects(7, 101), 107);

      expect(store.firstRow()).toBe(101);
      expect(store.lastRow()).toBe(107);
      expect(store.hasMore()).toBe(false);
    });

    it('numbers no rows when there are none', () => {
      store.show(FIRST);
      api.answer([], 0);

      expect(store.firstRow()).toBe(0);
      expect(store.lastRow()).toBe(0);
    });

    it('counts the pages at the size of the rows on screen', () => {
      store.show({ ...FIRST, size: 25 });
      api.answer(subjects(25), 101);

      expect(store.pages()).toBe(5);
    });

    it('keeps saying what is on screen while the next page is awaited', () => {
      // The pager marks the page asked for; the sentence names the rows that are there. Saying "subjects
      // 101-150" over rows 51-100 would be wrong for as long as the request takes.
      store.show({ ...FIRST, page: 2 });
      api.answer(subjects(50, 51), 200);

      store.show({ ...FIRST, page: 3 });

      expect(store.page()).toBe(3);
      expect(store.shownPage()).toBe(2);
      expect(store.firstRow()).toBe(51);
      expect(store.subjects()[0]?.name).toBe('s51');
    });

    it('has more after a page that is not the last, and none after the last', () => {
      store.show(FIRST);
      api.answer(subjects(50), 51);
      expect(store.hasMore()).toBe(true);

      store.show({ ...FIRST, page: 2 });
      api.answer(subjects(1, 51), 51);
      expect(store.hasMore()).toBe(false);
    });
  });

  describe('when the registry sent no count', () => {
    it('does not invent one: the pages are unknown', () => {
      store.show(FIRST);
      api.answer(subjects(50), null);

      expect(store.total()).toBeNull();
      expect(store.pages()).toBeNull();
    });

    it('says there may be more after a full page, and not after a short one', () => {
      store.show(FIRST);
      api.answer(subjects(50), null);
      expect(store.hasMore()).toBe(true);

      store.show({ ...FIRST, page: 2 });
      api.answer(subjects(12, 51), null);
      expect(store.hasMore()).toBe(false);
    });

    it('treats an empty later page as past the end, and goes back one', () => {
      store.show({ ...FIRST, page: 4 });
      api.answer([], null);

      expect(store.outOfRange()).toEqual({ requested: 4, last: 3 });
    });
  });

  describe('keeping the rows', () => {
    it('keeps the rows, flagged as searching, while another page of the same list is awaited', () => {
      store.show(FIRST);
      api.answer(subjects(50), 200);

      store.show({ ...FIRST, page: 2 });

      expect(store.subjects()).toHaveLength(50);
      expect(store.loading()).toBe(true);
      expect(store.searching()).toBe(true);
    });

    it('discards them for a fresh load: an environment switch, whose rows answer nothing now', () => {
      store.show(FIRST);
      api.answer(subjects(50), 200);

      store.show(FIRST, { fresh: true });

      expect(store.subjects()).toEqual([]);
      expect(store.searching()).toBe(false);
      expect(store.loaded()).toBe(false);
    });

    it('does not claim to be searching when there were no rows to keep', () => {
      store.show(FIRST);

      expect(store.searching()).toBe(false);
    });
  });

  describe('a page past the end', () => {
    it('is a redirect to the last page, and not a table with no rows', () => {
      store.show({ ...FIRST, page: 99 });
      api.answer([], 600);

      expect(store.outOfRange()).toEqual({ requested: 99, last: 12 });
      // No blank table and no "no subjects" panel in between: the screen has not got an answer yet.
      expect(store.loading()).toBe(true);
      expect(store.isEmpty()).toBe(false);
      expect(store.noMatches()).toBe(false);
      expect(store.subjects()).toEqual([]);
    });

    it('lands on the last page and remembers which one was asked for', () => {
      store.show({ ...FIRST, page: 99 });
      api.answer([], 600);

      store.show({ ...FIRST, page: 12 });
      api.answer(subjects(50, 551), 600);

      expect(store.clampedFrom()).toBe(99);
      expect(store.outOfRange()).toBeNull();
      expect(store.shownPage()).toBe(12);
      expect(store.loading()).toBe(false);
    });

    it('forgets that it was clamped at the next change the person makes', () => {
      store.show({ ...FIRST, page: 99 });
      api.answer([], 600);
      store.show({ ...FIRST, page: 12 });
      api.answer(subjects(50, 551), 600);

      store.show({ ...FIRST, page: 11 });

      expect(store.clampedFrom()).toBeNull();
    });

    it('does not call a different size a clamp', () => {
      // The last page of 25 is not the page 99 asked for, and the sentence would be about a size the
      // person has since changed.
      store.show({ ...FIRST, page: 99 });
      api.answer([], 600);

      store.show({ prefix: '', page: 12, size: 25 });

      expect(store.clampedFrom()).toBeNull();
    });

    it('does not call a different prefix a clamp', () => {
      store.show({ ...FIRST, page: 99 });
      api.answer([], 600);

      store.show({ prefix: 'acme.', page: 12, size: 50 });

      expect(store.clampedFrom()).toBeNull();
    });

    it('goes to the first page when the list has shrunk to nothing, and says so', () => {
      store.show({ prefix: 'zz', page: 3, size: 50 });
      api.answer([], 0);

      expect(store.outOfRange()).toEqual({ requested: 3, last: 1 });
    });

    it('never points forward: a count that contradicts the empty page is not followed', () => {
      // The registry says 5000 and gave nothing for page 2. Going to "page 100" because of the count would
      // walk away from the data; the previous page is the nearest one that can exist.
      store.show({ ...FIRST, page: 2 });
      api.answer([], 5000);

      expect(store.outOfRange()).toEqual({ requested: 2, last: 1 });
    });

    it('is not a redirect on the first page, where an empty answer is the answer', () => {
      store.show(FIRST);
      api.answer([], 0);

      expect(store.outOfRange()).toBeNull();
      expect(store.isEmpty()).toBe(true);
    });
  });

  describe('when the request fails', () => {
    it('captures the failure as state instead of throwing', () => {
      // The template renders the error. Letting it propagate would surface it as an
      // unhandled rejection in the console and leave the screen showing a spinner.
      store.show(FIRST);
      api.fail(new Error('offline'));

      expect(store.error()).toBeInstanceOf(ConcordatError);
      expect(store.loading()).toBe(false);
      expect(store.loaded()).toBe(true);
    });

    it('normalises whatever was thrown into a ConcordatError', () => {
      store.show(FIRST);
      api.fail('something odd');

      expect(store.error()?.code).toBe('registry_refused');
    });

    it('keeps a ConcordatError intact rather than re-wrapping it', () => {
      // The interceptor has already mapped it, and the code is the only thing a screen can
      // branch on to say something more useful than "it failed".
      const refusal = new ConcordatError({
        status: 404,
        code: 'subject_not_found',
        detail: 'No such environment.',
      });

      store.show(FIRST);
      api.fail(refusal);

      expect(store.error()).toBe(refusal);
    });

    it('clears the rows it was showing', () => {
      // Stale rows next to an error banner read as "here is the data, and also a problem",
      // which is the opposite of what happened.
      store.show(FIRST);
      api.answer([subject('orders.created')], 1);

      store.show({ ...FIRST, page: 2 });
      api.fail(new Error('offline'));

      expect(store.subjects()).toEqual([]);
    });

    it('clears the error when a new attempt starts', () => {
      store.show(FIRST);
      api.fail(new Error('offline'));

      store.show(FIRST);

      expect(store.error()).toBeNull();
      expect(store.loading()).toBe(true);
    });

    it('can still load afterwards', () => {
      // The reason `catchError` sits inside the `switchMap` and swallows. Placed outside,
      // the failure tears down the `rxMethod`'s own subscription and every later ask
      // does nothing at all — a screen whose retry button is permanently dead.
      store.show(FIRST);
      api.fail(new Error('offline'));

      store.show(FIRST);
      api.answer([subject('orders.created')], 1);

      expect(store.subjects().map((s) => s.name)).toEqual(['orders.created']);
      expect(store.error()).toBeNull();
    });

    it('retries what was being shown, from a clean slate', () => {
      store.show({ prefix: 'acme.', page: 3, size: 25 });
      api.fail(new Error('offline'));

      store.reload();

      expect(api.requests[1]).toEqual({
        environment: 'dev',
        prefix: 'acme.',
        limit: 25,
        offset: 50,
        sort: 'newest',
      });
      expect(store.error()).toBeNull();
      expect(store.loading()).toBe(true);
    });

    it('survives a source that fails synchronously', () => {
      TestBed.resetTestingModule();
      const failing = {
        listSubjects: () => throwError(() => new Error('config missing')),
      };
      TestBed.configureTestingModule({
        providers: [
          provideConcordatConfig(),
          { provide: SubjectsApi, useValue: failing },
          SubjectListStore,
        ],
      });

      const failed = TestBed.inject(SubjectListStore);
      failed.show(FIRST);

      expect(failed.error()).toBeInstanceOf(ConcordatError);
      expect(failed.loading()).toBe(false);
    });
  });

  describe('a late answer', () => {
    it('is dropped when another environment has been asked for since', () => {
      // Why `switchMap` and not `mergeMap`. Switching environment twice in quick succession
      // must not let the first environment's subjects land last and sit there labelled as the
      // second environment's.
      environments.select('dev');
      store.show(FIRST, { fresh: true });
      const slow = 0;

      environments.select('prod');
      store.show(FIRST, { fresh: true });

      api.answer([subject('prod.only')], 1, 1);
      api.answer([subject('dev.only')], 1, slow);

      expect(store.subjects().map((s) => s.name)).toEqual(['prod.only']);
    });

    it('is dropped when another page has been asked for since (ADR-052)', () => {
      // The page replaces ADR-045's epoch: a click on page 4 while page 3 is on its way must leave page 4
      // on screen when page 3 finally arrives.
      store.show(FIRST);
      api.answer(subjects(50), 500);

      store.show({ ...FIRST, page: 3 });
      store.show({ ...FIRST, page: 4 });

      api.answer(subjects(50, 151), 500, 2);
      api.answer(subjects(50, 101), 500, 1);

      expect(store.shownPage()).toBe(4);
      expect(store.subjects()[0]?.name).toBe('s151');
    });

    it('is dropped when another prefix has been asked for since', () => {
      store.show({ ...FIRST, prefix: 'ac' });
      store.show({ ...FIRST, prefix: 'acme.' });

      api.answer([subject('acme.a')], 1, 1);
      api.answer([subject('acme.a'), subject('acne.b')], 2, 0);

      expect(store.shown()).toBe('acme.');
      expect(store.subjects().map((s) => s.name)).toEqual(['acme.a']);
    });

    it('cannot turn a page that did exist into a redirect', () => {
      // The slow answer to page 99 (empty) arriving after page 12 was asked for must not flag page 99.
      store.show({ ...FIRST, page: 99 });
      store.show({ ...FIRST, page: 12 });

      api.answer(subjects(50, 551), 600, 1);
      api.answer([], 600, 0);

      expect(store.outOfRange()).toBeNull();
      expect(store.shownPage()).toBe(12);
    });
  });

  describe('noMatches', () => {
    it('stays true while the next answer to a prefix that is still finding nothing is awaited', () => {
      store.show({ ...FIRST, prefix: 'nothing.' });
      api.answer([], 0);

      store.show({ ...FIRST, prefix: 'nothing.at' });

      expect(store.noMatches()).toBe(true);
    });

    it('is not true for a failure', () => {
      store.show({ ...FIRST, prefix: 'acme.' });
      api.fail(new Error('offline'));

      expect(store.noMatches()).toBe(false);
    });
  });
});
