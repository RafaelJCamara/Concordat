import { TestBed } from '@angular/core/testing';
import { map, Subject as ResponseStream, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { Subject, SubjectPage } from '../../../domain/registry/subject';
import { SubjectsApi } from '../data-access/subjects-api';
import { PAGE_SIZE, SEARCH_DEBOUNCE_MS, SubjectListStore } from './subject-list-store';

// This is the reference store shape for M4.3, so the properties pinned here are the ones
// every feature store copied from it will inherit — including the two that are easy to get
// wrong: a failure has to become state rather than an exception, and the store has to still
// work afterwards.

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

/** A stand-in for `SubjectsApi` whose responses this file decides when to deliver. */
function fakeApi() {
  const streams: ResponseStream<readonly Subject[] | SubjectPage>[] = [];
  const asked: string[] = [];
  const prefixes: string[] = [];
  const requests: { prefix: string; limit: number | undefined; offset: number | undefined }[] = [];

  return {
    asked,
    /** The prefix each request carried, '' for none, in the order asked. */
    prefixes,
    /** Every request in full, to see the page each one asked for. */
    requests,
    streams,
    /** The most recent in-flight response, so a test can complete it on its own terms. */
    latest() {
      return streams[streams.length - 1]! as unknown as ResponseStream<readonly Subject[]>;
    },
    /** Answers the latest request with a page and the registry's own total. */
    answer(items: readonly Subject[], total: number) {
      (streams[streams.length - 1]! as ResponseStream<SubjectPage>).next({ items, total });
    },
    listSubjects(
      environment: string,
      options: { prefix?: string; limit?: number; offset?: number } = {},
    ) {
      asked.push(environment);
      prefixes.push(options.prefix ?? '');
      requests.push({ prefix: options.prefix ?? '', limit: options.limit, offset: options.offset });
      const stream = new ResponseStream<readonly Subject[] | SubjectPage>();
      streams.push(stream);

      // A bare list is a whole answer, so its total is its length; a page names its own.
      return stream.pipe(
        map((answer): SubjectPage =>
          'items' in answer ? answer : { items: answer, total: answer.length },
        ),
      );
    },
  };
}
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

  describe('isEmpty', () => {
    it('is false before anything has been asked for', () => {
      // "No subjects" and "nobody has looked yet" are different screens.
      expect(store.isEmpty()).toBe(false);
    });

    it('is false while a load is in flight', () => {
      // The specific flicker this computed prevents: an empty list plus a pending request
      // renders "no subjects in this environment" for as long as the request takes, and
      // then replaces it with rows.
      store.load();

      expect(store.loading()).toBe(true);
      expect(store.isEmpty()).toBe(false);
    });

    it('is true once a load has finished and returned nothing', () => {
      store.load();
      api.latest().next([]);

      expect(store.isEmpty()).toBe(true);
    });

    it('is false when the load returned rows', () => {
      store.load();
      api.latest().next([subject('orders.created')]);

      expect(store.isEmpty()).toBe(false);
    });
  });

  describe('load', () => {
    it('asks for the active environment', () => {
      environments.select('prod');

      store.load();

      expect(api.asked).toEqual(['prod']);
    });

    it('publishes the subjects and stops loading', () => {
      store.load();
      api.latest().next([subject('orders.created'), subject('orders.shipped')]);

      expect(store.subjects().map((s) => s.name)).toEqual(['orders.created', 'orders.shipped']);
      expect(store.loading()).toBe(false);
      expect(store.loaded()).toBe(true);
      expect(store.error()).toBeNull();
    });

    it('clears a previous error when a new attempt starts', () => {
      store.load();
      api.latest().error(new Error('offline'));

      store.load();

      expect(store.error()).toBeNull();
      expect(store.loading()).toBe(true);
    });
  });

  describe('when the request fails', () => {
    it('captures the failure as state instead of throwing', () => {
      // The template renders the error. Letting it propagate would surface it as an
      // unhandled rejection in the console and leave the screen showing a spinner.
      store.load();
      api.latest().error(new Error('offline'));

      expect(store.error()).toBeInstanceOf(ConcordatError);
      expect(store.loading()).toBe(false);
      expect(store.loaded()).toBe(true);
    });

    it('normalises whatever was thrown into a ConcordatError', () => {
      store.load();
      api.latest().error('something odd');

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

      store.load();
      api.latest().error(refusal);

      expect(store.error()).toBe(refusal);
    });

    it('clears the rows it was showing', () => {
      // Stale rows next to an error banner read as "here is the data, and also a problem",
      // which is the opposite of what happened.
      store.load();
      api.latest().next([subject('orders.created')]);

      store.load();
      api.latest().error(new Error('offline'));

      expect(store.subjects()).toEqual([]);
    });

    it('can still load afterwards', () => {
      // The reason `catchError` sits inside the `switchMap` and swallows. Placed outside,
      // the failure tears down the `rxMethod`'s own subscription and every later `load()`
      // does nothing at all — a screen whose retry button is permanently dead, with no
      // error to show for it.
      store.load();
      api.latest().error(new Error('offline'));

      store.load();
      api.latest().next([subject('orders.created')]);

      expect(store.subjects().map((s) => s.name)).toEqual(['orders.created']);
      expect(store.error()).toBeNull();
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
      failed.load();

      expect(failed.error()).toBeInstanceOf(ConcordatError);
      expect(failed.loading()).toBe(false);
    });
  });

  it('discards a slow response that a newer request has superseded', () => {
    // Why `switchMap` and not `mergeMap`. Switching environment twice in quick succession
    // must not let the first environment's subjects land last and sit there labelled as the
    // second environment's.
    environments.select('dev');
    store.load();
    const slow = api.streams[0]!;

    environments.select('prod');
    store.load();
    const fast = api.streams[1]!;

    fast.next([subject('prod.only')]);
    slow.next([subject('dev.only')]);

    expect(store.subjects().map((s) => s.name)).toEqual(['prod.only']);
  });

  // The registry filters (ADR-038); this store only decides when to ask, which request is still
  // wanted, and what the screen should say about the answer.
  describe('search', () => {
    beforeEach(() => vi.useFakeTimers());
    afterEach(() => vi.useRealTimers());

    /** A list that has loaded, so a search starts from a screen with rows on it. */
    function loadedWith(...names: string[]) {
      store.load();
      api.latest().next(names.map(subject));
    }

    const pause = () => vi.advanceTimersByTime(SEARCH_DEBOUNCE_MS);

    it('does not ask at once: typing is not asking', () => {
      loadedWith('acme.orders.A', 'acme.billing.B');

      store.search('a');
      store.search('ac');
      store.search('acm');

      expect(api.asked).toHaveLength(1);
      vi.advanceTimersByTime(SEARCH_DEBOUNCE_MS - 1);
      expect(api.asked).toHaveLength(1);
    });

    it('asks once, for what was typed last, when the typing pauses', () => {
      loadedWith('acme.orders.A', 'acme.billing.B');

      store.search('a');
      store.search('ac');
      store.search('acme.o');
      pause();

      expect(api.prefixes).toEqual(['', 'acme.o']);
      expect(api.asked).toEqual(['dev', 'dev']);
    });

    it('asks for the active environment', () => {
      environments.select('prod');
      loadedWith('prod.only');

      store.search('prod.');
      pause();

      expect(api.asked).toEqual(['prod', 'prod']);
    });

    it('trims the prefix as the registry will, and does not ask again for the same one', () => {
      loadedWith('acme.orders.A');

      store.search('acme.');
      pause();
      store.search('  acme. ');
      store.search('acme.');
      pause();

      expect(api.prefixes).toEqual(['', 'acme.']);
    });

    it('keeps the case it was given: names are case-sensitive', () => {
      loadedWith('acme.orders.A');

      store.search('Acme.');
      pause();

      expect(api.prefixes[1]).toBe('Acme.');
    });

    it('sends what it was given even when no name could match, and lets the registry say so', () => {
      // The registry is the only judge. A client that skipped the request for "acme-" would be a
      // second implementation of the answer, and the first thing to drift from the first.
      loadedWith('acme.orders.A');

      store.search('acme-');
      pause();

      expect(api.prefixes).toEqual(['', 'acme-']);
    });

    it('asks for everything again when the box is cleared', () => {
      loadedWith('acme.orders.A', 'zeta.B');
      store.search('acme.');
      pause();
      api.latest().next([subject('acme.orders.A')]);

      store.search('');
      pause();

      expect(api.prefixes).toEqual(['', 'acme.', '']);
    });

    it('keeps the rows on screen, and says it is searching, while the answer is awaited', () => {
      // Replacing the list with a skeleton on every pause in typing would flash the table; the
      // rows that are there are the best answer available until the right ones arrive.
      loadedWith('acme.orders.A', 'zeta.B');

      store.search('acme.');
      pause();

      expect(store.loading()).toBe(true);
      expect(store.searching()).toBe(true);
      expect(store.loaded()).toBe(true);
      expect(store.subjects().map((s) => s.name)).toEqual(['acme.orders.A', 'zeta.B']);
    });

    it('is not searching during a plain load, which has no rows to keep', () => {
      store.load();

      expect(store.loading()).toBe(true);
      expect(store.searching()).toBe(false);
    });

    it('publishes the answer, and remembers which prefix it answers', () => {
      loadedWith('acme.orders.A', 'zeta.B');
      store.search('acme.');
      pause();

      api.latest().next([subject('acme.orders.A')]);

      expect(store.subjects().map((s) => s.name)).toEqual(['acme.orders.A']);
      expect(store.shown()).toBe('acme.');
      expect(store.loading()).toBe(false);
      expect(store.searching()).toBe(false);
    });

    it('still shows the old prefix as the answered one while a newer one is only typed', () => {
      // What "No subject names start with ..." is allowed to say: it names the prefix the rows
      // answer, and not the half-typed one that nobody has asked about yet.
      loadedWith('acme.orders.A');
      store.search('acme.');
      pause();
      api.latest().next([subject('acme.orders.A')]);

      store.search('acme.b');

      expect(store.prefix()).toBe('acme.b');
      expect(store.shown()).toBe('acme.');
    });

    it('cancels the request a newer one supersedes, and ignores its late answer', () => {
      loadedWith('acme.orders.A', 'zeta.B');
      store.search('a');
      pause();
      const stale = api.latest();

      store.search('ac');
      pause();
      const wanted = api.latest();

      // The HTTP request itself is abandoned, not merely ignored: a slow search must not hold a
      // connection open for an answer nobody will read.
      expect(stale.observed).toBe(false);
      expect(wanted.observed).toBe(true);

      wanted.next([subject('acme.orders.A')]);
      stale.next([subject('zeta.B')]);

      expect(store.subjects().map((s) => s.name)).toEqual(['acme.orders.A']);
      expect(store.shown()).toBe('ac');
    });

    it('keeps the earlier request alive until the pause ends and the new one is actually asked', () => {
      loadedWith('acme.orders.A');
      store.search('a');
      pause();
      const first = api.latest();

      store.search('ac');

      // Still waiting out the pause: the earlier question is still the live one.
      expect(first.observed).toBe(true);

      pause();
      expect(first.observed).toBe(false);
    });

    it('treats a reload as the stronger request: it asks now, with the prefix typed so far', () => {
      // An environment switch or a retry must not wait out a pause, and must not drop what was
      // typed; and the pause still running must not ask a second time afterwards.
      loadedWith('acme.orders.A');
      store.search('acme.');

      store.load();

      expect(api.prefixes).toEqual(['', 'acme.']);
      expect(store.searching()).toBe(false);

      pause();
      expect(api.prefixes).toEqual(['', 'acme.']);
    });

    it('asks a reload in a new environment for the same prefix', () => {
      loadedWith('dev.orders.A');
      store.search('dev.');
      pause();
      api.latest().next([subject('dev.orders.A')]);

      environments.select('prod');
      store.load();

      expect(api.asked).toEqual(['dev', 'dev', 'prod']);
      expect(api.prefixes).toEqual(['', 'dev.', 'dev.']);
    });

    describe('isEmpty and noMatches', () => {
      it('an empty answer to no prefix is an empty environment', () => {
        loadedWith();

        expect(store.isEmpty()).toBe(true);
        expect(store.noMatches()).toBe(false);
      });

      it('an empty answer to a prefix is a prefix that matched nothing, and not an empty environment', () => {
        // The distinction the screen turns on. "No subjects yet" offers to create the first one and
        // hides the search box; showing it for a prefix that merely found nothing would take the
        // box away from the person who needs it to type something else.
        loadedWith('acme.orders.A');
        store.search('nothing.');
        pause();
        api.latest().next([]);

        expect(store.isEmpty()).toBe(false);
        expect(store.noMatches()).toBe(true);
        expect(store.shown()).toBe('nothing.');
      });

      it('is neither while the first answer to a prefix is awaited', () => {
        loadedWith('acme.orders.A');
        store.search('nothing.');
        pause();

        expect(store.isEmpty()).toBe(false);
        expect(store.noMatches()).toBe(false);
      });

      it('stays what the screen shows while the next answer is awaited, so the panel does not flash', () => {
        loadedWith('acme.orders.A');
        store.search('nothing.');
        pause();
        api.latest().next([]);

        store.search('nothing.at');
        pause();

        expect(store.loading()).toBe(true);
        expect(store.noMatches()).toBe(true);
        expect(store.isEmpty()).toBe(false);
      });

      it('is neither when the answer is rows', () => {
        loadedWith('acme.orders.A');
        store.search('acme.');
        pause();
        api.latest().next([subject('acme.orders.A')]);

        expect(store.isEmpty()).toBe(false);
        expect(store.noMatches()).toBe(false);
      });

      it('is not a prefix that matched nothing when the search failed', () => {
        loadedWith('acme.orders.A');
        store.search('acme.');
        pause();
        api.latest().error(new Error('offline'));

        expect(store.noMatches()).toBe(false);
        expect(store.error()).toBeInstanceOf(ConcordatError);
      });
    });

    describe('when a search fails', () => {
      it('becomes state, clears the rows, and a later search works', () => {
        loadedWith('acme.orders.A');
        store.search('acme.');
        pause();
        api.latest().error(new Error('offline'));

        expect(store.error()).toBeInstanceOf(ConcordatError);
        expect(store.subjects()).toEqual([]);
        expect(store.loading()).toBe(false);
        expect(store.searching()).toBe(false);

        store.search('zeta.');
        pause();
        api.latest().next([subject('zeta.B')]);

        expect(store.error()).toBeNull();
        expect(store.subjects().map((s) => s.name)).toEqual(['zeta.B']);
      });

      it('is retried for the same prefix, not for everything', () => {
        loadedWith('acme.orders.A');
        store.search('acme.');
        pause();
        api.latest().error(new Error('offline'));

        store.load();

        expect(api.prefixes).toEqual(['', 'acme.', 'acme.']);
      });
    });
  });

  describe('a list longer than one page (ADR-045)', () => {
    // The prefix test waits out the search pause, so time is the test's to move.
    beforeEach(() => vi.useFakeTimers());
    afterEach(() => vi.useRealTimers());

    const many = (from: number, count: number) =>
      Array.from({ length: count }, (_, i) =>
        subject(`acme.s${String(from + i).padStart(3, '0')}.Thing`),
      );

    it('asks for the first page, in the registry’s page size', () => {
      store.load();

      expect(api.requests[0]).toEqual({ prefix: '', limit: PAGE_SIZE, offset: undefined });
    });

    it('keeps the total the registry named, and knows how much it has not read', () => {
      store.load();
      api.answer(many(0, 50), 120);

      expect(store.subjects()).toHaveLength(50);
      expect(store.total()).toBe(120);
      expect(store.unread()).toBe(70);
    });

    it('has nothing unread when the page is the whole list', () => {
      store.load();
      api.latest().next(many(0, 3));

      expect(store.unread()).toBe(0);
    });

    it('reads the next page after what is on screen, and appends it', () => {
      store.load();
      api.answer(many(0, 50), 80);

      store.loadMore();

      expect(api.requests[1]).toEqual({ prefix: '', limit: PAGE_SIZE, offset: 50 });

      api.answer(many(50, 30), 80);

      expect(store.subjects()).toHaveLength(80);
      expect(store.subjects()[79]?.name).toBe('acme.s079.Thing');
      expect(store.unread()).toBe(0);
      expect(store.loadingMore()).toBe(false);
    });

    it('asks for the prefix the rows answer, not the one typed since', () => {
      store.load();
      store.search('acme.');
      vi.advanceTimersByTime(SEARCH_DEBOUNCE_MS);
      api.answer(many(0, 50), 80);

      // Typing on, before the answer to the new text, must not make the next page that text's.
      store.search('zzz');
      store.loadMore();

      expect(api.requests.at(-1)).toMatchObject({ prefix: 'acme.', offset: 50 });
    });

    it('does not ask when everything has been read, or before the first answer', () => {
      store.loadMore();
      expect(api.requests).toHaveLength(0);

      store.load();
      store.loadMore();
      expect(api.requests).toHaveLength(1);

      api.latest().next(many(0, 3));
      store.loadMore();
      expect(api.requests).toHaveLength(1);
    });

    it('ignores a second request while one is in flight', () => {
      store.load();
      api.answer(many(0, 50), 120);

      store.loadMore();
      store.loadMore();

      expect(api.requests).toHaveLength(2);
      expect(store.loadingMore()).toBe(true);
    });

    it('does not show the same subject twice when the pages overlap', () => {
      // Somebody created a subject that sorts into the first page between the two reads.
      store.load();
      api.answer(many(0, 50), 100);
      store.loadMore();

      api.answer([...many(49, 1), ...many(50, 50)], 101);

      expect(store.subjects()).toHaveLength(100);
      expect(new Set(store.subjects().map((s) => s.name)).size).toBe(100);
    });

    it('keeps what was read when a later page fails, and says so', () => {
      store.load();
      api.answer(many(0, 50), 120);
      store.loadMore();

      api
        .latest()
        .error(new ConcordatError({ status: 500, code: 'x', detail: 'page two is down' }));

      expect(store.subjects()).toHaveLength(50);
      expect(store.moreError()?.detail).toBe('page two is down');
      expect(store.error()).toBeNull();
      expect(store.loadingMore()).toBe(false);
    });

    it('can ask again after a page failed', () => {
      store.load();
      api.answer(many(0, 50), 120);
      store.loadMore();
      api.latest().error(new Error('offline'));

      store.loadMore();

      expect(api.requests).toHaveLength(3);
      expect(store.moreError()).toBeNull();
    });

    it('drops a page that was answered after the list was replaced', () => {
      store.load();
      api.answer(many(0, 50), 120);
      store.loadMore();
      const stale = api.streams[1] as unknown as ResponseStream<readonly Subject[]>;

      // A reload replaces the list while page two is still on its way.
      store.load();
      api.answer(many(100, 5), 5);
      stale.next(many(50, 50));

      expect(store.subjects().map((s) => s.name)).toEqual(many(100, 5).map((s) => s.name));
      expect(store.loadingMore()).toBe(false);
    });
  });
});
