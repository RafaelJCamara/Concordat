import { signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';
import { SessionStore } from '../../../core/auth/session-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { Scope } from '../../../domain/identity/scope';
import type { PendingApproval } from '../../../domain/registry/approval-queue';
import {
  keyOf,
  type Decision,
  type DecisionState,
  PendingApprovalsStore,
} from '../application/pending-approvals-store';
import { DiffStore } from '../application/diff-store';
import { ImpactStore } from '../application/impact-store';
import { ApprovalsPage } from './approvals-page';
import { PendingReview } from './pending-review';

// The screen's own decisions: that the controls are absent for someone who cannot use them,
// that deciding takes two clicks and says what it does, and that a row leaving the queue is
// announced rather than just gone.

const held: PendingApproval = {
  subject: 'orders.created',
  ordinal: 2,
  semanticVersion: '2.0.0',
  changelog: 'drops customerId',
  registeredAt: new Date('2026-08-13T09:00:00Z'),
  registeredBy: 'alice@example.com',
  baseline: 1,
};
function storeIn(state: {
  pending?: readonly PendingApproval[];
  loading?: boolean;
  loaded?: boolean;
  isEmpty?: boolean;
  total?: number;
  loadingMore?: boolean;
  moreError?: ConcordatError | null;
  refreshFailed?: boolean;
  error?: ConcordatError | null;
  decisions?: Record<string, DecisionState>;
  lastDecided?: Decision | null;
}) {
  const decide = vi.fn();
  const loadMore = vi.fn();
  const refresh = vi.fn();
  const pending = state.pending ?? [];
  const total = state.total ?? pending.length;

  return {
    decide,
    loadMore,
    refresh,
    store: {
      pending: signal(pending),
      total: signal(total),
      unread: signal(Math.max(total - pending.length, 0)),
      loadingMore: signal(state.loadingMore ?? false),
      moreError: signal(state.moreError ?? null),
      refreshFailed: signal(state.refreshFailed ?? false),
      loading: signal(state.loading ?? false),
      loaded: signal(state.loaded ?? true),
      isEmpty: signal(state.isEmpty ?? false),
      error: signal(state.error ?? null),
      decisions: signal(state.decisions ?? {}),
      lastDecided: signal(state.lastDecided ?? null),
      load: () => {},
      loadMore,
      refresh,
      decide,
    } as unknown as InstanceType<typeof PendingApprovalsStore>,
  };
}

function idleStore() {
  return {
    diff: signal(null),
    report: signal(null),
    loading: signal(true),
    error: signal(null),
    load: () => {},
  };
}

function render(
  state: Parameters<typeof storeIn>[0],
  scopes: readonly Scope[] = ['subject:admin'],
): {
  fixture: ComponentFixture<ApprovalsPage>;
  host: HTMLElement;
  decide: ReturnType<typeof vi.fn>;
  loadMore: ReturnType<typeof vi.fn>;
  refresh: ReturnType<typeof vi.fn>;
} {
  const { store, decide, loadMore, refresh } = storeIn(state);

  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });

  TestBed.overrideComponent(ApprovalsPage, {
    set: { providers: [{ provide: PendingApprovalsStore, useValue: store }] },
  });

  // An opened review would otherwise build the real diff and impact stores. These sit on their
  // loading state, which is all this file needs of them — the review has its own spec.
  TestBed.overrideComponent(PendingReview, {
    set: {
      providers: [
        { provide: DiffStore, useValue: idleStore() },
        { provide: ImpactStore, useValue: idleStore() },
      ],
    },
  });

  // After the overrides: `inject` instantiates the test module, and a component cannot be
  // overridden once it has been.
  TestBed.inject(SessionStore).signIn({ credential: 'c', actor: 'alice', scopes });

  const fixture = TestBed.createComponent(ApprovalsPage);
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, decide, loadMore, refresh };
}

function button(host: HTMLElement, text: string): HTMLButtonElement | undefined {
  return [...host.querySelectorAll('button')].find((b) => b.textContent?.trim() === text);
}

describe('ApprovalsPage', () => {
  it('names the subject and version, and links to both', () => {
    const { host } = render({ pending: [held] });

    expect(host.querySelector('a[href="/subjects/orders.created"]')).not.toBeNull();
    expect(host.querySelector('a[href="/subjects/orders.created/versions/2"]')).not.toBeNull();
  });

  it('shows who registered it, what it says, and what it would replace', () => {
    const { host } = render({ pending: [held] });

    expect(host.textContent).toContain('alice@example.com');
    expect(host.textContent).toContain('drops customerId');
    expect(host.textContent).toContain('replaces v1');
  });

  it('says there is nothing to decide when the queue is empty', () => {
    const { host } = render({ isEmpty: true });

    expect(host.textContent).toContain('Nothing is waiting on a decision');
  });

  it('shows a skeleton while the first load is in flight', () => {
    const { host } = render({ loading: true, loaded: false });

    expect(host.querySelector('[aria-busy="true"]')).not.toBeNull();
  });

  it('renders a failed load with a retry, rather than an empty queue', () => {
    const { host } = render({
      error: new ConcordatError({ status: 500, code: 'x', detail: 'The registry is down.' }),
    });

    expect(host.textContent).toContain('Could not load the queue');
    expect(host.textContent).toContain('The registry is down.');
    expect(host.textContent).not.toContain('Nothing is waiting on a decision');
  });

  describe('who may decide', () => {
    it('offers approve and reject to an admin', () => {
      const { host } = render({ pending: [held] }, ['subject:admin']);

      expect(button(host, 'Approve')).toBeDefined();
      expect(button(host, 'Reject')).toBeDefined();
    });

    it('omits them for a reader, rather than disabling them (ADR-018)', () => {
      const { host } = render({ pending: [held] }, ['subject:read']);

      expect(button(host, 'Approve')).toBeUndefined();
      expect(button(host, 'Reject')).toBeUndefined();
    });

    it('omits them for a writer, who may register the change but not decide it', () => {
      // The author of a breaking change approving it is the arrangement the gate exists to
      // prevent, which is why the approval scopes are narrower than the write ones.
      const { host } = render({ pending: [held] }, ['subject:write']);

      expect(button(host, 'Approve')).toBeUndefined();
    });

    it('still lets a reader open the review', () => {
      const { host } = render({ pending: [held] }, ['subject:read']);

      expect(button(host, 'Review changes and impact')).toBeDefined();
    });
  });

  describe('deciding takes two clicks', () => {
    it('does not decide on the first click, and says what approving does', () => {
      const { fixture, host, decide } = render({ pending: [held] });

      button(host, 'Approve')!.click();
      fixture.detectChanges();

      expect(decide).not.toHaveBeenCalled();
      expect(host.textContent).toContain('Approve v2 and make it latest?');
    });

    it('decides on the second click', () => {
      const { fixture, host, decide } = render({ pending: [held] });

      button(host, 'Approve')!.click();
      fixture.detectChanges();
      button(host, 'Confirm')!.click();

      expect(decide).toHaveBeenCalledWith({
        subject: 'orders.created',
        ordinal: 2,
        action: 'approve',
      });
    });

    it('says rejecting cannot be undone, and sends a reject', () => {
      const { fixture, host, decide } = render({ pending: [held] });

      button(host, 'Reject')!.click();
      fixture.detectChanges();

      expect(host.textContent).toContain('Reject v2? This cannot be undone.');

      button(host, 'Confirm')!.click();

      expect(decide).toHaveBeenCalledWith({
        subject: 'orders.created',
        ordinal: 2,
        action: 'reject',
      });
    });

    it('sends nothing when cancelled', () => {
      const { fixture, host, decide } = render({ pending: [held] });

      button(host, 'Approve')!.click();
      fixture.detectChanges();
      button(host, 'Cancel')!.click();
      fixture.detectChanges();

      expect(decide).not.toHaveBeenCalled();
      expect(button(host, 'Approve')).toBeDefined();
    });
  });

  describe('while and after deciding', () => {
    it('disables the controls for a version being decided', () => {
      const { host } = render({
        pending: [held],
        decisions: { [keyOf('orders.created', 2)]: { state: 'deciding', action: 'approve' } },
      });

      expect(button(host, 'Approve')!.disabled).toBe(true);
      expect(button(host, 'Reject')!.disabled).toBe(true);
    });

    it('shows a refusal on the row, with the registry’s reason', () => {
      const { host } = render({
        pending: [held],
        decisions: {
          [keyOf('orders.created', 2)]: {
            state: 'failed',
            action: 'approve',
            error: new ConcordatError({
              status: 403,
              code: 'insufficient_scope',
              detail: 'Needs subject:admin.',
            }),
          },
        },
      });

      expect(host.textContent).toContain('Could not approve this version');
      expect(host.textContent).toContain('Needs subject:admin.');
    });

    it('announces what happened to a row that left the queue', () => {
      const { host } = render({
        isEmpty: true,
        lastDecided: { subject: 'orders.created', ordinal: 2, action: 'approve' },
      });

      const status = host.querySelector('[role="status"]');

      expect(status?.textContent).toContain('Approved');
      expect(status?.textContent).toContain('orders.created v2');
      expect(status?.textContent).toContain('latest');
    });

    it('does not claim a rejected version became latest', () => {
      const { host } = render({
        isEmpty: true,
        lastDecided: { subject: 'orders.created', ordinal: 2, action: 'reject' },
      });

      const status = host.querySelector('[role="status"]');

      expect(status?.textContent).toContain('Rejected');
      expect(status?.textContent).not.toContain('latest');
    });
  });

  describe('the review', () => {
    it('is closed until asked for, so a long queue does not fetch for rows nobody opened', () => {
      const { host } = render({ pending: [held] });

      expect(host.querySelector('cd-pending-review')).toBeNull();
      expect(button(host, 'Review changes and impact')!.getAttribute('aria-expanded')).toBe(
        'false',
      );
    });

    it('opens and closes, and says so to assistive technology', () => {
      const { fixture, host } = render({ pending: [held] });

      button(host, 'Review changes and impact')!.click();
      fixture.detectChanges();

      expect(host.querySelector('cd-pending-review')).not.toBeNull();
      expect(button(host, 'Hide changes and impact')!.getAttribute('aria-expanded')).toBe('true');

      button(host, 'Hide changes and impact')!.click();
      fixture.detectChanges();

      expect(host.querySelector('cd-pending-review')).toBeNull();
    });
  });

  describe('a queue longer than the screen has read', () => {
    it('says how much of it is shown, and offers the rest', () => {
      const { host } = render({ pending: [held], total: 120 });

      const extent = host.querySelector('[data-testid="queue-extent"]');

      expect(extent?.textContent).toContain('Showing the 1 oldest of 120 waiting');
      expect(extent?.textContent).toContain('119 more are not shown yet');
      expect(button(host, 'Show more')).toBeDefined();
    });

    it('says "is" for the last one left', () => {
      const { host } = render({ pending: [held], total: 2 });

      expect(host.textContent).toContain('1 more is not shown yet');
    });

    it('asks the store for the next page when asked', () => {
      const { host, loadMore } = render({ pending: [held], total: 120 });

      button(host, 'Show more')!.click();

      expect(loadMore).toHaveBeenCalledTimes(1);
    });

    it('says nothing about extent when the whole queue is on screen', () => {
      const { host } = render({ pending: [held], total: 1 });

      expect(host.querySelector('[data-testid="queue-extent"]')).toBeNull();
      expect(button(host, 'Show more')).toBeUndefined();
    });

    it('disables the button while a page is on its way', () => {
      const { host } = render({ pending: [held], total: 120, loadingMore: true });

      expect(button(host, 'Loading…')!.disabled).toBe(true);
    });

    it('reports a page that could not be read, and keeps the ones that were', () => {
      const { host } = render({
        pending: [held],
        total: 120,
        moreError: new ConcordatError({ status: 500, code: 'x', detail: 'page two is down' }),
      });

      expect(host.textContent).toContain('Could not load more');
      expect(host.textContent).toContain('page two is down');
      expect(host.querySelector('a[href="/subjects/orders.created"]')).not.toBeNull();
    });
  });

  describe('after a decision, when the queue could not be read again', () => {
    it('warns that what is shown may be out of date, and keeps showing it', () => {
      const { host } = render({ pending: [held], refreshFailed: true });

      expect(host.textContent).toContain('The queue could not be refreshed');
      expect(host.querySelector('a[href="/subjects/orders.created"]')).not.toBeNull();
    });

    it('offers to try again', () => {
      const { host, refresh } = render({ pending: [held], refreshFailed: true });

      button(host, 'Try again')!.click();

      expect(refresh).toHaveBeenCalledTimes(1);
    });

    it('says nothing when the refresh worked', () => {
      const { host } = render({ pending: [held] });

      expect(host.textContent).not.toContain('could not be refreshed');
    });
  });
});
