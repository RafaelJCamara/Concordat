import { signal, type WritableSignal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ConcordatError } from '../../../core/http/problem-details';
import type { VersionDiff } from '../../../domain/registry/diff';
import type { PendingApproval } from '../../../domain/registry/approval-queue';
import type { ImpactReport } from '../../../domain/registry/impact';
import type { SchemaDiffResult } from '../../../domain/registry/schema-diff';
import { DiffStore } from '../application/diff-store';
import { ImpactStore } from '../application/impact-store';
import { REVIEW_VIEW_STORAGE_KEY } from '../application/review-view-store';
import { SchemaDiffStore } from '../application/schema-diff-store';
import { PendingReview } from './pending-review';
import { PendingSchemaDiff } from './pending-schema-diff';

// The point of this component is that it joins two stores' answers for one version. What is
// worth proving is which requests it makes, and what it says when there is nothing to say.

const diff: VersionDiff = {
  from: 1,
  to: 2,
  fromSchemaId: 'schema-1',
  toSchemaId: 'schema-2',
  identical: false,
  policy: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
  divergences: [
    {
      path: '/required',
      kind: 'required_field_added',
      direction: 'BACKWARD',
      surface: 'WIRE_JSON',
      message: "'total' is now required.",
      conflictsWithVersion: 1,
    },
  ],
};

const impact: ImpactReport = {
  subject: 'orders.created',
  candidateOrdinal: 2,
  candidateSchemaId: 'schema-2',
  breakingCount: 1,
  consumers: [
    {
      service: 'billing',
      selector: '1',
      readerOrdinal: 1,
      breaks: true,
      certainty: 'CHECKED',
      lastSeenAt: new Date('2026-08-13T09:00:00Z'),
      stale: false,
      reasons: ['type changed'],
    },
  ],
  contracts: [],
};

interface Answers {
  diff?: VersionDiff | null;
  diffLoading?: boolean;
  diffError?: ConcordatError | null;
  impact?: ImpactReport | null;
  impactLoading?: boolean;
  impactError?: ConcordatError | null;
  /** What the detailed view's own store says, for the specs that open it. */
  lineDiff?: SchemaDiffResult | null;
}

function render(
  pending: PendingApproval,
  answers: Answers = {},
): {
  fixture: ComponentFixture<PendingReview>;
  host: HTMLElement;
  diffLoad: ReturnType<typeof vi.fn>;
  impactLoad: ReturnType<typeof vi.fn>;
  lineDiffLoad: ReturnType<typeof vi.fn>;
  lineDiff: WritableSignal<SchemaDiffResult | null>;
} {
  const diffLoad = vi.fn();
  const impactLoad = vi.fn();
  const lineDiffLoad = vi.fn();
  const lineDiff = signal<SchemaDiffResult | null>(answers.lineDiff ?? null);

  TestBed.configureTestingModule({ providers: [provideRouter([])] });

  TestBed.overrideComponent(PendingReview, {
    set: {
      providers: [
        {
          provide: DiffStore,
          useValue: {
            diff: signal('diff' in answers ? answers.diff : diff),
            loading: signal(answers.diffLoading ?? false),
            error: signal(answers.diffError ?? null),
            load: diffLoad,
          },
        },
        {
          provide: ImpactStore,
          useValue: {
            report: signal('impact' in answers ? answers.impact : impact),
            loading: signal(answers.impactLoading ?? false),
            error: signal(answers.impactError ?? null),
            load: impactLoad,
          },
        },
      ],
    },
  });

  // The detailed view owns a store of its own, created only when it is shown.
  TestBed.overrideComponent(PendingSchemaDiff, {
    set: {
      providers: [
        {
          provide: SchemaDiffStore,
          useValue: {
            loading: signal(false),
            result: lineDiff,
            problem: signal(null),
            load: lineDiffLoad,
          },
        },
      ],
    },
  });

  const fixture = TestBed.createComponent(PendingReview);
  fixture.componentRef.setInput('pending', pending);
  fixture.detectChanges();

  return {
    fixture,
    host: fixture.nativeElement as HTMLElement,
    diffLoad,
    impactLoad,
    lineDiffLoad,
    lineDiff,
  };
}

const withBaseline: PendingApproval = {
  subject: 'orders.created',
  ordinal: 2,
  semanticVersion: null,
  changelog: null,
  registeredAt: new Date('2026-08-13T09:00:00Z'),
  registeredBy: 'ci',
  baseline: 1,
  reviewers: [],
  mayDecide: true,
};

describe('PendingReview', () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => localStorage.clear());

  it('diffs the held version against what consumers resolve today', () => {
    const { diffLoad } = render(withBaseline);

    expect(diffLoad).toHaveBeenCalledWith({ subject: 'orders.created', from: '1', to: '2' });
  });

  it('asks about the impact of the held version itself, not of latest', () => {
    const { impactLoad } = render(withBaseline);

    expect(impactLoad).toHaveBeenCalledWith({ subject: 'orders.created', ordinal: '2' });
  });

  it('shows the changes, headed by the version they are measured from', () => {
    const { host } = render(withBaseline);

    expect(host.textContent).toContain('Changes from v1');
    expect(host.textContent).toContain('/required');
  });

  it('shows who breaks, with the registry’s reason', () => {
    const { host } = render(withBaseline);

    expect(host.textContent).toContain('1 of 1 breaks');
    expect(host.textContent).toContain('billing');
    expect(host.textContent).toContain('type changed');
  });

  it('says so, and requests no diff, when nothing has ever been approved', () => {
    const { host, diffLoad } = render({ ...withBaseline, baseline: null });

    expect(host.textContent).toContain('Nothing to compare against');
    expect(diffLoad).not.toHaveBeenCalled();
  });

  it('does not read an empty list of consumers as safety', () => {
    const { host } = render(withBaseline, {
      impact: { ...impact, breakingCount: 0, consumers: [] },
    });

    expect(host.textContent).toContain('not a finding that nobody is affected');
  });

  it('says plainly when no rule reports a difference', () => {
    const { host } = render(withBaseline, { diff: { ...diff, divergences: [] } });

    expect(host.textContent).toContain('No compatibility rule reports a difference from v1');
  });

  it('shows a skeleton for each half that is still loading', () => {
    const { host } = render(withBaseline, {
      diff: null,
      diffLoading: true,
      impact: null,
      impactLoading: true,
    });

    expect(host.querySelectorAll('[aria-busy="true"]').length).toBe(2);
  });

  it('reports a failure of one half without losing the other', () => {
    const { host } = render(withBaseline, {
      diff: null,
      diffError: new ConcordatError({ status: 500, code: 'x', detail: 'diff is down' }),
    });

    expect(host.textContent).toContain('Could not load the changes');
    expect(host.textContent).toContain('diff is down');
    expect(host.textContent).toContain('billing');
  });

  it('links to the full screens for both', () => {
    const { host } = render(withBaseline);

    expect(
      host.querySelector('a[href="/subjects/orders.created/versions/1/diff/2"]'),
    ).not.toBeNull();
    expect(
      host.querySelector('a[href="/subjects/orders.created/versions/2/impact"]'),
    ).not.toBeNull();
  });
});

// The choice between the line diff and the summary (ADR-050). The line diff itself is
// `schema-diff.spec.ts` and the component that draws it is `schema-diff.spec.ts` in `ui/`; what is
// pinned here is the choice: what it starts as, that it is a real control, that it is
// remembered, and that a line diff that cannot be shown never leaves the reviewer with nothing.

const readyDiff: SchemaDiffResult = {
  status: 'ready',
  segments: [
    {
      kind: 'rows',
      rows: [
        { kind: 'removed', oldNumber: 3, newNumber: null, text: '"type": "string"', findings: [] },
        { kind: 'added', oldNumber: null, newNumber: 3, text: '"type": "integer"', findings: [0] },
      ],
    },
  ],
  lines: [],
  added: 1,
  removed: 1,
  unplaced: [],
};

describe('PendingReview: detailed or summary', () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => localStorage.clear());

  const radio = (host: HTMLElement, name: string): HTMLInputElement =>
    [...host.querySelectorAll<HTMLInputElement>('input[type="radio"]')].find(
      (input) => input.closest('label')?.textContent?.trim() === name,
    ) as HTMLInputElement;

  it('offers the choice as a radio group with both options, labelled', () => {
    const { host } = render(withBaseline);
    const group = host.querySelector('[role="radiogroup"]');

    expect(group?.getAttribute('aria-label')).toBe('Show the changes as');
    expect(group?.querySelectorAll('input[type="radio"]')).toHaveLength(2);
    expect(radio(host, 'Detailed')).toBeTruthy();
    expect(radio(host, 'Summary')).toBeTruthy();
  });

  it('starts on the summary, so a first visit shows what the screen always showed', () => {
    const { host, lineDiffLoad } = render(withBaseline);

    expect(radio(host, 'Summary').checked).toBe(true);
    expect(radio(host, 'Detailed').checked).toBe(false);
    expect(host.textContent).toContain('/required');
    expect(host.querySelector('cd-pending-schema-diff')).toBeNull();
    expect(lineDiffLoad).not.toHaveBeenCalled();
  });

  it('shows the line diff, and not the list, once Detailed is chosen', () => {
    const { fixture, host, lineDiffLoad } = render(withBaseline, { lineDiff: readyDiff });

    radio(host, 'Detailed').click();
    fixture.detectChanges();

    expect(host.querySelector('cd-pending-schema-diff')).not.toBeNull();
    expect(host.querySelector('cd-schema-diff')).not.toBeNull();
    expect(host.querySelector('cd-divergence-list')).toBeNull();
    expect(radio(host, 'Detailed').checked).toBe(true);
    // Fetched for this pair only: the schema ids of the diff the review already held.
    expect(lineDiffLoad).toHaveBeenCalledWith({
      from: 'schema-1',
      to: 'schema-2',
      divergences: diff.divergences,
    });
  });

  it('remembers the choice for the next visit', () => {
    const first = render(withBaseline, { lineDiff: readyDiff });
    radio(first.host, 'Detailed').click();
    first.fixture.detectChanges();

    expect(localStorage.getItem(REVIEW_VIEW_STORAGE_KEY)).toBe('detailed');

    TestBed.resetTestingModule();
    const { host } = render(withBaseline, { lineDiff: readyDiff });

    expect(radio(host, 'Detailed').checked).toBe(true);
    expect(host.querySelector('cd-schema-diff')).not.toBeNull();
  });

  it('can be put back on the summary, and remembers that too', () => {
    localStorage.setItem(REVIEW_VIEW_STORAGE_KEY, 'detailed');
    const { fixture, host } = render(withBaseline, { lineDiff: readyDiff });

    radio(host, 'Summary').click();
    fixture.detectChanges();

    expect(host.querySelector('cd-pending-schema-diff')).toBeNull();
    expect(host.textContent).toContain('/required');
    expect(localStorage.getItem(REVIEW_VIEW_STORAGE_KEY)).toBe('summary');
  });

  it('gives each review its own group, so choosing in one cannot clear another', () => {
    const one = render(withBaseline);
    TestBed.resetTestingModule();
    const two = render(withBaseline);

    expect(radio(one.host, 'Summary').name).not.toBe(radio(two.host, 'Summary').name);
    expect(radio(one.host, 'Summary').name).toBe(radio(one.host, 'Detailed').name);
  });

  it('falls back to the summary, and says why, when the line diff is too large', () => {
    localStorage.setItem(REVIEW_VIEW_STORAGE_KEY, 'detailed');
    const { fixture, host } = render(withBaseline, {
      lineDiff: { status: 'too-large', reason: 'changes', limit: 1000 },
    });
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="diff-fallback"]')?.textContent).toContain(
      'More than 1,000 lines changed',
    );
    expect(host.querySelector('cd-schema-diff')).toBeNull();
    // The list the reviewer would have had under Summary is there.
    expect(host.textContent).toContain('/required');
    // The choice itself is untouched: the next, smaller change is shown in detail again.
    expect(radio(host, 'Detailed').checked).toBe(true);
  });

  it('shows the summary stand-in while the version diff the detail is built from is loading', () => {
    localStorage.setItem(REVIEW_VIEW_STORAGE_KEY, 'detailed');
    const { host } = render(withBaseline, { diff: null, diffLoading: true });

    expect(host.querySelector('cd-pending-schema-diff')).toBeNull();
    expect(host.querySelectorAll('[aria-busy="true"]').length).toBeGreaterThan(0);
  });

  it('keeps the choice, and the registry’s error, when the version diff cannot be loaded', () => {
    localStorage.setItem(REVIEW_VIEW_STORAGE_KEY, 'detailed');
    const { host } = render(withBaseline, {
      diff: null,
      diffError: new ConcordatError({ status: 500, code: 'x', detail: 'diff is down' }),
    });

    expect(host.textContent).toContain('diff is down');
    expect(radio(host, 'Detailed').checked).toBe(true);
  });

  it('offers no choice when there is nothing to compare against', () => {
    const { host } = render({ ...withBaseline, baseline: null });

    expect(host.querySelector('[role="radiogroup"]')).toBeNull();
  });

  it('still asks for the impact whichever view is chosen', () => {
    localStorage.setItem(REVIEW_VIEW_STORAGE_KEY, 'detailed');
    const { impactLoad, host } = render(withBaseline, { lineDiff: readyDiff });

    expect(impactLoad).toHaveBeenCalledOnce();
    expect(host.textContent).toContain('billing');
  });
});
