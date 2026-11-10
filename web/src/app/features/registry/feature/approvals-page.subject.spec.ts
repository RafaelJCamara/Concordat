import { signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SessionStore } from '../../../core/auth/session-store';
import { ActiveEnvironmentStore } from '../../../core/config/active-environment-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { PendingApproval } from '../../../domain/registry/approval-queue';
import { DiffStore } from '../application/diff-store';
import { ImpactStore } from '../application/impact-store';
import { PendingApprovalsStore } from '../application/pending-approvals-store';
import { ApprovalsPage } from './approvals-page';
import { PendingReview } from './pending-review';

// The address can narrow the approvals screen to one subject and point at one of its versions
// (ADR-052): that is where the subjects list sends the pill that says something is held. What is
// pinned here is that the screen says it is narrowed and how to undo that, says so honestly when the
// subject has nothing held, and takes the reader to the version they were sent for exactly once.
// The queue itself, and deciding, are the other spec's.

function held(subject: string, ordinal: number): PendingApproval {
  return {
    subject,
    ordinal,
    semanticVersion: null,
    changelog: null,
    registeredAt: new Date('2026-08-13T09:00:00Z'),
    registeredBy: 'alice@example.com',
    baseline: 1,
    reviewers: [],
    mayDecide: true,
  };
}

function stubStore(pending: readonly PendingApproval[] = []) {
  const state = {
    pending: signal(pending),
    loading: signal(false),
    loaded: signal(true),
    error: signal<ConcordatError | null>(null),
  };

  return {
    ...state,
    total: signal(pending.length),
    unread: signal(0),
    loadingMore: signal(false),
    moreError: signal(null),
    refreshFailed: signal(false),
    isEmpty: signal(pending.length === 0),
    decisions: signal({}),
    lastDecided: signal(null),
    load: vi.fn(),
    loadMore: vi.fn(),
    refresh: vi.fn(),
    decide: vi.fn(),
  };
}

type Stub = ReturnType<typeof stubStore>;

function render(store: Stub, address: Record<string, string | string[]> = {}) {
  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });
  TestBed.overrideComponent(ApprovalsPage, {
    set: {
      providers: [
        {
          provide: PendingApprovalsStore,
          useValue: store as unknown as InstanceType<typeof PendingApprovalsStore>,
        },
      ],
    },
  });
  const idle = {
    diff: signal(null),
    report: signal(null),
    loading: signal(true),
    error: signal(null),
    load: () => {},
  };
  TestBed.overrideComponent(PendingReview, {
    set: {
      providers: [
        { provide: DiffStore, useValue: idle },
        { provide: ImpactStore, useValue: idle },
      ],
    },
  });
  TestBed.inject(SessionStore).signIn({
    credential: 'c',
    actor: 'alice',
    scopes: ['subject:admin'],
  });

  const fixture = TestBed.createComponent(ApprovalsPage);

  for (const [name, value] of Object.entries(address)) {
    fixture.componentRef.setInput(name, value);
  }

  // Attached, so that focus can be asserted: a detached element cannot hold it.
  document.body.appendChild(fixture.nativeElement as HTMLElement);
  fixture.detectChanges();

  return fixture;
}

describe('ApprovalsPage: narrowed to a subject by the address', () => {
  let fixture: ComponentFixture<ApprovalsPage> | undefined;

  afterEach(() => {
    (fixture?.nativeElement as HTMLElement | undefined)?.remove();
    fixture = undefined;
  });

  const host = () => fixture!.nativeElement as HTMLElement;
  const text = (selector: string) =>
    host().querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim() ?? null;
  const toggle = (subject: string, ordinal: number) =>
    heading(subject, ordinal)
      ?.closest('li')
      ?.querySelector<HTMLButtonElement>('button[aria-expanded]');
  const heading = (subject: string, ordinal: number) =>
    host().querySelector<HTMLElement>(`[data-pending-key="${subject}@${ordinal}"]`);

  describe('asking for the queue', () => {
    it('asks for all of it when the address names no subject', () => {
      const store = stubStore([held('orders.created', 2)]);
      fixture = render(store);

      expect(store.load).toHaveBeenCalledExactlyOnceWith(null);
      expect(host().querySelector('[data-testid="approvals-filter"]')).toBeNull();
    });

    it('asks for the subject the address names, and only that', () => {
      const store = stubStore([held('orders.created', 2)]);
      fixture = render(store, { subject: 'orders.created' });

      expect(store.load).toHaveBeenCalledExactlyOnceWith('orders.created');
    });

    it('trims the name as the registry will, and reads a blank one as none', () => {
      const store = stubStore();
      fixture = render(store, { subject: '  orders.created  ' });
      expect(store.load).toHaveBeenLastCalledWith('orders.created');

      fixture.componentRef.setInput('subject', '   ');
      fixture.detectChanges();
      expect(store.load).toHaveBeenLastCalledWith(null);
    });

    it('reads the first of a name the address repeated', () => {
      const store = stubStore();
      fixture = render(store, { subject: ['a.b', 'c.d'] });

      expect(store.load).toHaveBeenLastCalledWith('a.b');
    });

    it('asks again when the address changes to another subject, and when it stops narrowing', () => {
      const store = stubStore();
      fixture = render(store, { subject: 'a.b' });

      fixture.componentRef.setInput('subject', 'c.d');
      fixture.detectChanges();
      expect(store.load).toHaveBeenLastCalledWith('c.d');

      fixture.componentRef.setInput('subject', undefined);
      fixture.detectChanges();
      expect(store.load).toHaveBeenLastCalledWith(null);
    });

    it('asks again, for the same subject, when the environment changes', () => {
      const store = stubStore();
      fixture = render(store, { subject: 'a.b' });
      store.load.mockClear();

      TestBed.inject(ActiveEnvironmentStore).select('prod');
      fixture.detectChanges();

      expect(store.load).toHaveBeenCalledExactlyOnceWith('a.b');
    });

    it('keeps the subject when the load is retried', () => {
      const store = stubStore();
      store.error.set(new ConcordatError({ status: 500, code: 'x', detail: 'down' }));
      fixture = render(store, { subject: 'a.b' });
      store.load.mockClear();

      [...host().querySelectorAll('button')]
        .find((b) => b.textContent?.includes('Try again'))
        ?.click();

      expect(store.load).toHaveBeenCalledExactlyOnceWith('a.b');
    });

    it('takes a name with dots, slashes and characters that mean something in an address, whole', () => {
      const name = 'acme.orders/v2 & co=1?#x%20é';
      const store = stubStore();
      fixture = render(store, { subject: name });

      expect(store.load).toHaveBeenLastCalledWith(name);
      expect(text('[data-testid="approvals-filter"]')).toContain(name);
    });
  });

  describe('saying that it is narrowed', () => {
    it('says which subject the list is about, beside a way back to all of it', () => {
      fixture = render(stubStore([held('orders.created', 2)]), { subject: 'orders.created' });

      expect(text('[data-testid="approvals-filter"]')).toContain(
        'Showing changes waiting on orders.created',
      );
      const showAll = [...host().querySelectorAll('a')].find(
        (a) => a.textContent?.trim() === 'Show all',
      );

      expect(showAll?.getAttribute('href')).toBe('/approvals');
    });

    it('is a status, so a screen reader hears that the list is narrowed', () => {
      fixture = render(stubStore([held('orders.created', 2)]), { subject: 'orders.created' });

      expect(host().querySelector('[data-testid="approvals-filter"]')?.getAttribute('role')).toBe(
        'status',
      );
    });
  });

  describe('a subject with nothing held', () => {
    it('says so, and that it may already have been decided, with a way to the rest', () => {
      fixture = render(stubStore([]), { subject: 'orders.created' });

      expect(text('[data-testid="approvals-filter-empty"]')).toContain(
        'Nothing is waiting on a decision for orders.created — it may already have been decided',
      );
      expect(
        [...host().querySelectorAll('[data-testid="approvals-filter-empty"] a')].map((a) =>
          a.getAttribute('href'),
        ),
      ).toEqual(['/approvals']);
    });

    it('is not the sentence for an empty queue, which says something else is true', () => {
      fixture = render(stubStore([]), { subject: 'orders.created' });

      expect(host().textContent).not.toContain('A breaking change registered against a subject');
    });

    it('leaves the empty queue as it was when there is no filter', () => {
      fixture = render(stubStore([]));

      expect(host().textContent).toContain('Nothing is waiting on a decision');
      expect(host().querySelector('[data-testid="approvals-filter-empty"]')).toBeNull();
    });
  });

  describe('landing on a version', () => {
    it('opens the review of the version the address names, and puts the focus on its heading', () => {
      fixture = render(stubStore([held('orders.created', 2), held('orders.created', 3)]), {
        subject: 'orders.created',
        version: '3',
      });

      expect(toggle('orders.created', 3)?.getAttribute('aria-expanded')).toBe('true');
      expect(toggle('orders.created', 2)?.getAttribute('aria-expanded')).toBe('false');
      expect(document.activeElement).toBe(heading('orders.created', 3));
    });

    it('scrolls the heading into view', () => {
      const scroll = vi.fn();
      const original = HTMLElement.prototype.scrollIntoView;
      HTMLElement.prototype.scrollIntoView = scroll;

      try {
        fixture = render(stubStore([held('orders.created', 2)]), {
          subject: 'orders.created',
          version: '2',
        });

        expect(scroll).toHaveBeenCalledTimes(1);
        expect(scroll.mock.contexts[0]).toBe(heading('orders.created', 2));
      } finally {
        HTMLElement.prototype.scrollIntoView = original;
      }
    });

    it('takes the focus without trapping it: the heading is not in the tab order, and Tab moves on from it', () => {
      fixture = render(stubStore([held('orders.created', 2)]), {
        subject: 'orders.created',
        version: '2',
      });

      expect(heading('orders.created', 2)?.getAttribute('tabindex')).toBe('-1');
      // Nothing holds the focus there: no key handler, no inert siblings.
      expect(host().querySelectorAll('[inert]')).toHaveLength(0);
    });

    it('waits for the queue: nothing is opened, or focused, while it is still being read', () => {
      const store = stubStore([held('orders.created', 2)]);
      store.loading.set(true);
      store.loaded.set(false);
      fixture = render(store, { subject: 'orders.created', version: '2' });

      expect(heading('orders.created', 2)).toBeNull();

      store.loading.set(false);
      store.loaded.set(true);
      fixture.detectChanges();

      expect(toggle('orders.created', 2)?.getAttribute('aria-expanded')).toBe('true');
      expect(document.activeElement).toBe(heading('orders.created', 2));
    });

    it('does it once: a refresh of the queue does not reopen what the reader closed', () => {
      const store = stubStore([held('orders.created', 2), held('orders.created', 3)]);
      fixture = render(store, { subject: 'orders.created', version: '2' });

      toggle('orders.created', 2)?.click();
      fixture.detectChanges();
      expect(toggle('orders.created', 2)?.getAttribute('aria-expanded')).toBe('false');

      store.pending.set([
        held('orders.created', 2),
        held('orders.created', 3),
        held('orders.created', 4),
      ]);
      fixture.detectChanges();

      expect(toggle('orders.created', 2)?.getAttribute('aria-expanded')).toBe('false');
    });

    it('follows the address to another version', () => {
      fixture = render(stubStore([held('orders.created', 2), held('orders.created', 3)]), {
        subject: 'orders.created',
        version: '2',
      });

      fixture.componentRef.setInput('version', '3');
      fixture.detectChanges();

      expect(toggle('orders.created', 3)?.getAttribute('aria-expanded')).toBe('true');
      expect(document.activeElement).toBe(heading('orders.created', 3));
    });

    it('says so when the version named is not held any more, and lists what is', () => {
      fixture = render(stubStore([held('orders.created', 3)]), {
        subject: 'orders.created',
        version: '2',
      });

      expect(text('[data-testid="approvals-target-gone"]')).toContain(
        'orders.created v2 is not waiting on a decision any more',
      );
      expect(toggle('orders.created', 3)?.getAttribute('aria-expanded')).toBe('false');
    });

    it('does not say it when the version is there', () => {
      fixture = render(stubStore([held('orders.created', 2)]), {
        subject: 'orders.created',
        version: '2',
      });

      expect(host().querySelector('[data-testid="approvals-target-gone"]')).toBeNull();
    });

    it.each(['abc', '0', '-1', '2.5', '', '1e1', '99999999999'])(
      'ignores version %j, which is not a place in the list',
      (version) => {
        fixture = render(stubStore([held('orders.created', 2)]), {
          subject: 'orders.created',
          version,
        });

        expect(toggle('orders.created', 2)?.getAttribute('aria-expanded')).toBe('false');
        expect(host().querySelector('[data-testid="approvals-target-gone"]')).toBeNull();
      },
    );

    it('ignores a version with no subject to look for it in', () => {
      fixture = render(stubStore([held('orders.created', 2)]), { version: '2' });

      expect(toggle('orders.created', 2)?.getAttribute('aria-expanded')).toBe('false');
    });

    it('leaves the unfiltered queue alone: nothing opens, nothing is focused', () => {
      fixture = render(stubStore([held('orders.created', 2)]));

      expect(toggle('orders.created', 2)?.getAttribute('aria-expanded')).toBe('false');
      expect(document.activeElement).toBe(document.body);
    });
  });
});
