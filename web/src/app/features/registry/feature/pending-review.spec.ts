import { signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';
import { ConcordatError } from '../../../core/http/problem-details';
import type { VersionDiff } from '../../../domain/registry/diff';
import type { ImpactReport } from '../../../domain/registry/impact';
import type { SchemaVersion } from '../../../domain/registry/subject';
import { DiffStore } from '../application/diff-store';
import { ImpactStore } from '../application/impact-store';
import type { PendingApproval } from '../application/pending-approvals-store';
import { PendingReview } from './pending-review';

// The point of this component is that it joins two stores' answers for one version. What is
// worth proving is which requests it makes, and what it says when there is nothing to say.

function version(ordinal: number): SchemaVersion {
  return {
    ordinal,
    schemaId: `schema-${ordinal}`,
    semanticVersion: null,
    status: ordinal === 1 ? 'ACTIVE' : 'AWAITING_APPROVAL',
    changelog: null,
    registeredAt: new Date('2026-08-13T09:00:00Z'),
    registeredBy: 'ci',
    deprecated: false,
  };
}

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
}

function render(
  pending: PendingApproval,
  answers: Answers = {},
): {
  fixture: ComponentFixture<PendingReview>;
  host: HTMLElement;
  diffLoad: ReturnType<typeof vi.fn>;
  impactLoad: ReturnType<typeof vi.fn>;
} {
  const diffLoad = vi.fn();
  const impactLoad = vi.fn();

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

  const fixture = TestBed.createComponent(PendingReview);
  fixture.componentRef.setInput('pending', pending);
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, diffLoad, impactLoad };
}

const withBaseline: PendingApproval = {
  subject: 'orders.created',
  version: version(2),
  baseline: version(1),
};

describe('PendingReview', () => {
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
