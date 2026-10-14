import { signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { SessionStore } from '../../../core/auth/session-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { Scope } from '../../../domain/identity/scope';
import type {
  EnvironmentSummary,
  HeldVersion,
  NewestSubject,
} from '../../../domain/registry/environment-summary';
import { DashboardStore } from '../application/dashboard-store';
import { DashboardPage } from './dashboard-page';

// Every number on this screen is the registry's, so the arithmetic is not this component's to test
// (the .NET reader's tests own it). What is the component's: which number goes under which title,
// what it says when there is nothing, that a number that has not arrived is never drawn as a zero,
// and that the page never derives one itself.

function newest(name: string): NewestSubject {
  return {
    name,
    format: 'json',
    owner: 'orders-team',
    lifecycle: 'ACTIVE',
    latest: 1,
    versions: 1,
    awaitingApproval: 0,
    createdAt: new Date('2026-10-01T09:00:00.000Z'),
  };
}

function held(subject: string, status: HeldVersion['status'] = 'AWAITING_APPROVAL'): HeldVersion {
  return {
    subject,
    ordinal: 2,
    status,
    semanticVersion: null,
    registeredAt: new Date('2026-10-14T09:00:00.000Z'),
    registeredBy: 'ci',
    decidedAt: status === 'REJECTED' ? new Date('2026-10-14T10:00:00.000Z') : null,
    decidedBy: status === 'REJECTED' ? 'bob' : null,
  };
}

function summary(overrides: Partial<EnvironmentSummary> = {}): EnvironmentSummary {
  return {
    subjects: { total: 2, active: 2, deprecated: 0, retired: 0 },
    versions: { total: 5, active: 4, awaitingApproval: 1, rejected: 0, dismissed: 0 },
    newestSubjects: [newest('orders.created'), newest('orders.shipped')],
    breakingChanges: [held('orders.created')],
    enforcement: [
      { mode: 'OFF', contracts: 0, publishBindings: 0, consumeBindings: 0 },
      { mode: 'MONITOR', contracts: 1, publishBindings: 2, consumeBindings: 1 },
      { mode: 'ENFORCE', contracts: 3, publishBindings: 4, consumeBindings: 2 },
    ],
    violations: { distinct: 6, recent: 2, windowHours: 24 },
    ...overrides,
  };
}

const EMPTY: EnvironmentSummary = summary({
  subjects: { total: 0, active: 0, deprecated: 0, retired: 0 },
  versions: { total: 0, active: 0, awaitingApproval: 0, rejected: 0, dismissed: 0 },
  newestSubjects: [],
  breakingChanges: [],
  enforcement: [
    { mode: 'OFF', contracts: 0, publishBindings: 0, consumeBindings: 0 },
    { mode: 'MONITOR', contracts: 0, publishBindings: 0, consumeBindings: 0 },
    { mode: 'ENFORCE', contracts: 0, publishBindings: 0, consumeBindings: 0 },
  ],
  violations: { distinct: 0, recent: 0, windowHours: 24 },
});

/** The stat card with this title, as the text a reader would see on it. */
function card(host: HTMLElement, title: string): string {
  const match = [...host.querySelectorAll('cd-stat-card')].find((element) =>
    element.textContent?.includes(title),
  );

  return match?.textContent ?? '';
}

interface StoreState {
  readonly summary?: EnvironmentSummary | null;
  readonly loading?: boolean;
  readonly error?: ConcordatError | null;
  readonly loaded?: boolean;
}

/**
 * A `DashboardStore`, stubbed.
 *
 * The store is replaced rather than the API underneath it, because the boundaries rule says a
 * routed component may not reach into `data-access`, and a test that imports `SummaryApi` to build
 * a fake has done, from the same folder, the thing the rule exists to prevent. The store is this
 * page's only collaborator, so it is also the only thing worth substituting.
 */
function fakeStore(state: StoreState) {
  const loads: number[] = [];
  const current = state.summary === undefined ? null : state.summary;
  const loading = state.loading ?? false;
  const loaded = state.loaded ?? (current !== null || (state.error ?? null) !== null);

  return {
    loads,
    store: {
      summary: signal(current),
      loading: signal(loading),
      error: signal(state.error ?? null),
      isEmpty: signal(loaded && !loading && current?.subjects.total === 0),
      load: () => loads.push(1),
    } as unknown as InstanceType<typeof DashboardStore>,
  };
}

function render(
  state: StoreState,
  scopes: readonly Scope[] = ['subject:write'],
): {
  fixture: ComponentFixture<DashboardPage>;
  host: HTMLElement;
  loads: number[];
} {
  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });

  const { store, loads } = fakeStore(state);

  // The page provides its own store, so a root-level provider would never be reached. This
  // replaces the component-level one, which is the only place it is registered.
  TestBed.overrideComponent(DashboardPage, {
    set: { providers: [{ provide: DashboardStore, useValue: store }] },
  });
  TestBed.inject(SessionStore).observeInstance({ claimed: true, actor: 'someone', scopes });

  const fixture = TestBed.createComponent(DashboardPage);
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, loads };
}

describe('DashboardPage', () => {
  it('shows the registry’s counts under their own titles', () => {
    const { host } = render({
      summary: summary({
        subjects: { total: 12, active: 12, deprecated: 0, retired: 0 },
        versions: { total: 40, active: 37, awaitingApproval: 3, rejected: 0, dismissed: 0 },
        violations: { distinct: 9, recent: 0, windowHours: 24 },
      }),
    });

    expect(card(host, 'Subjects')).toContain('12');
    expect(card(host, 'Versions')).toContain('40');
    expect(card(host, 'Awaiting approval')).toContain('3');
    expect(card(host, 'Violations')).toContain('9');
  });

  it('counts only what is awaiting approval under that title, not what was rejected', () => {
    // A rejected version is a decision that has been made, and counting it here would keep a
    // resolved thing on somebody's to-do list forever. The count is the registry's, and this pins
    // that the page reads the right member of it.
    const { host } = render({
      summary: summary({
        versions: { total: 6, active: 2, awaitingApproval: 1, rejected: 3, dismissed: 0 },
      }),
    });

    expect(card(host, 'Awaiting approval')).toContain('1');
    expect(card(host, 'Awaiting approval')).not.toContain('3');
  });

  it('says nothing is waiting rather than leaving a bare zero', () => {
    // A zero on an approvals counter reads equally as "nothing needs approving" and "approvals
    // are not switched on".
    const { host } = render({
      summary: summary({
        versions: { total: 4, active: 4, awaitingApproval: 0, rejected: 0, dismissed: 0 },
      }),
    });

    expect(card(host, 'Awaiting approval')).toContain('Nothing waiting');
  });

  it('notes the subjects that are not ordinary, so the total agrees with the list', () => {
    const { host } = render({
      summary: summary({ subjects: { total: 7, active: 4, deprecated: 2, retired: 1 } }),
    });

    expect(card(host, 'Subjects')).toContain('7');
    expect(card(host, 'Subjects')).toContain('2 deprecated');
    expect(card(host, 'Subjects')).toContain('1 retired');
  });

  it('keeps the plain subtitle when every subject is active', () => {
    const { host } = render({ summary: summary() });

    expect(card(host, 'Subjects')).toContain('Message contracts');
    expect(card(host, 'Subjects')).not.toContain('retired');
  });

  describe('violations', () => {
    it('says what was reported in the window, in the registry’s words', () => {
      const { host } = render({
        summary: summary({ violations: { distinct: 6, recent: 2, windowHours: 24 } }),
      });

      expect(card(host, 'Violations')).toContain('2 seen in the last 24 hours');
    });

    it('says none were reported, and not that none happened', () => {
      // Only an SDK can see a violation, and only one that reports. "No violations" would claim an
      // absence the registry cannot observe.
      const { host } = render({
        summary: summary({ violations: { distinct: 0, recent: 0, windowHours: 24 } }),
      });

      expect(card(host, 'Violations')).toContain('None reported in the last 24 hours');
      expect(card(host, 'Violations')).not.toContain('No violations');
    });
  });

  describe('while it loads', () => {
    it('draws a dash for every number, never a zero', () => {
      // "0 awaiting approval" on a screen still loading says nothing is waiting, which is the one
      // claim it cannot yet make.
      const { host } = render({ loading: true });

      for (const title of ['Subjects', 'Versions', 'Awaiting approval', 'Violations']) {
        expect(card(host, title)).toContain('—');
        expect(card(host, title)).not.toMatch(/\b0\b/);
      }
    });

    it('shows placeholders where the panels and the cards will be', () => {
      const { host } = render({ loading: true });

      expect(host.querySelectorAll('[aria-busy="true"]').length).toBeGreaterThan(0);
      expect(host.querySelector('cd-breaking-changes-panel')).toBeNull();
      expect(host.querySelector('cd-enforcement-panel')).toBeNull();
    });
  });

  describe('when the registry could not be reached', () => {
    const error = new ConcordatError({
      status: 500,
      code: 'registry_refused',
      detail: 'The registry is unreachable.',
    });

    it('says so, with the reason, instead of drawing numbers', () => {
      const { host } = render({ error });

      expect(host.textContent).toContain('Could not load this environment');
      expect(host.textContent).toContain('The registry is unreachable.');
      expect(host.querySelector('cd-stat-card')).toBeNull();
    });

    it('offers to try again, and the offer loads', () => {
      const { host, loads } = render({ error });
      const before = loads.length;

      host.querySelector<HTMLButtonElement>('button')?.click();

      expect(host.querySelector('button')?.textContent).toContain('Try again');
      expect(loads.length).toBe(before + 1);
    });
  });

  describe('the panels', () => {
    it('shows the breaking changes the registry listed', () => {
      const { host } = render({
        summary: summary({ breakingChanges: [held('orders.created'), held('orders.shipped')] }),
      });

      const panel = host.querySelector('cd-breaking-changes-panel');

      expect(panel?.textContent).toContain('orders.created');
      expect(panel?.textContent).toContain('orders.shipped');
    });

    it('shows enforcement coverage', () => {
      const { host } = render({ summary: summary() });

      const panel = host.querySelector('cd-enforcement-panel');

      expect(panel?.textContent).toContain('Enforcement coverage');
      expect(panel?.textContent).toContain('3 contracts');
    });
  });

  describe('the newest subjects', () => {
    it('shows the cards in the order the registry gave, and does not sort them', () => {
      // The registry orders by creation time and bounds the list. A page that re-sorted would be a
      // second opinion about which is newest, from data (the cards) that does not carry the
      // registry's tie-break.
      const { host } = render({
        summary: summary({
          newestSubjects: [newest('c.newest'), newest('a.middle'), newest('b.oldest')],
        }),
      });

      const names = [...host.querySelectorAll('cd-subject-card h3')].map((h) =>
        h.textContent?.trim(),
      );

      expect(names).toEqual(['c.newest', 'a.middle', 'b.oldest']);
    });

    it('links each card to its subject, and the list to all of them', () => {
      const { host } = render({ summary: summary() });

      expect(host.querySelector('a[href="/subjects/orders.created"]')).not.toBeNull();
      // The Subjects stat card also links to /subjects, so the link is found by what it says.
      const viewAll = [...host.querySelectorAll('a')].find((a) =>
        a.textContent?.includes('View all'),
      );

      expect(viewAll?.getAttribute('href')).toBe('/subjects');
    });
  });

  it('loads the overview when it opens', () => {
    const { loads } = render({ summary: summary() });

    expect(loads.length).toBeGreaterThan(0);
  });
});

// An empty registry is what a first run looks like, and the sentence that says "nothing is wrong"
// is only half of what a first run needs: the other half is the way out. Absent, not disabled, for
// somebody who could not use it (ADR-018).
describe('DashboardPage: an empty registry', () => {
  const create = (host: HTMLElement) => host.querySelector('a[href="/subjects/new-subject"]');

  it('explains itself instead of showing four zeroes and nothing else', () => {
    const { host } = render({ summary: EMPTY });

    expect(host.textContent).toContain('No subjects yet');
  });

  it('still says what each panel is for, rather than leaving a hole', () => {
    const { host } = render({ summary: EMPTY });

    expect(host.querySelector('cd-breaking-changes-panel')?.textContent).toContain(
      'Nothing held or rejected',
    );
    expect(host.querySelector('cd-enforcement-panel')?.textContent).toContain(
      'No contracts in this environment',
    );
  });

  it('offers to create the first subject, to someone who can write', () => {
    const { host } = render({ summary: EMPTY }, ['subject:write']);

    expect(create(host)?.textContent).toContain('Create the first subject');
  });

  it('says nothing about it to a reader, who is still told the registry is simply empty', () => {
    const { host } = render({ summary: EMPTY }, ['subject:read']);

    expect(host.textContent).toContain('No subjects yet');
    expect(create(host)).toBeNull();
  });

  it('is not offered once there is something to look at', () => {
    const { host } = render({ summary: summary() }, ['subject:write']);

    expect(create(host)).toBeNull();
  });
});
