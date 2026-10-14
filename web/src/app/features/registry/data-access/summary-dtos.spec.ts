import { describe, expect, it } from 'vitest';
import { ConcordatError } from '../../../core/http/problem-details';
import {
  toEnvironmentSummary,
  toHeldVersion,
  toNewestSubject,
  type EnvironmentSummaryDto,
  type HeldVersionDto,
  type NewestSubjectDto,
} from './summary-dtos';

// The boundary guards are the point: a token this build does not know has to refuse to draw, not be
// coerced into a neighbour, for the reason `wire-token.ts` gives. The held-version status is the
// sharp one, because the two statuses it admits are the two the panel paints differently.

const held: HeldVersionDto = {
  subject: 'orders.created',
  ordinal: 3,
  status: 'AWAITING_APPROVAL',
  semanticVersion: '2.0.0',
  registeredAt: '2026-10-14T09:00:00+00:00',
  registeredBy: 'ci',
  decidedAt: null,
  decidedBy: null,
};

const newest: NewestSubjectDto = {
  name: 'orders.created',
  format: 'json',
  owner: 'orders-team',
  lifecycle: 'ACTIVE',
  latest: 2,
  versions: 3,
  awaitingApproval: 1,
  createdAt: '2026-10-01T09:00:00+00:00',
};

const dto: EnvironmentSummaryDto = {
  subjects: { total: 3, active: 2, deprecated: 1, retired: 0 },
  versions: { total: 5, active: 3, awaitingApproval: 1, rejected: 1, dismissed: 0 },
  newestSubjects: [newest],
  breakingChanges: [held],
  enforcement: [
    { mode: 'OFF', contracts: 0, publishBindings: 0, consumeBindings: 0 },
    { mode: 'MONITOR', contracts: 1, publishBindings: 2, consumeBindings: 1 },
    { mode: 'ENFORCE', contracts: 2, publishBindings: 3, consumeBindings: 0 },
  ],
  violations: { distinct: 4, recent: 1, windowHours: 24 },
};

describe('toHeldVersion', () => {
  it('parses the timestamps and leaves an undecided version undecided', () => {
    const version = toHeldVersion(held);

    expect(version.registeredAt).toBeInstanceOf(Date);
    expect(version.registeredAt.toISOString()).toBe('2026-10-14T09:00:00.000Z');
    expect(version.decidedAt).toBeNull();
    expect(version.decidedBy).toBeNull();
  });

  it('parses a rejection', () => {
    const version = toHeldVersion({
      ...held,
      status: 'REJECTED',
      decidedAt: '2026-10-14T10:00:00+00:00',
      decidedBy: 'bob',
    });

    expect(version.status).toBe('REJECTED');
    expect(version.decidedAt?.toISOString()).toBe('2026-10-14T10:00:00.000Z');
    expect(version.decidedBy).toBe('bob');
  });

  it.each(['ACTIVE', 'DISMISSED', 'PENDING', 'awaiting_approval', ''])(
    'refuses %j, which is not a held or rejected version',
    (status) => {
      // ACTIVE and DISMISSED are valid VersionStatus tokens and are still not something this list
      // ever carries. Accepting them would paint a live version as a breaking change.
      expect(() => toHeldVersion({ ...held, status })).toThrow(ConcordatError);
    },
  );

  it('refuses a timestamp that is not one', () => {
    expect(() => toHeldVersion({ ...held, registeredAt: 'yesterday' })).toThrow(ConcordatError);
  });
});

describe('toNewestSubject', () => {
  it('maps the card fields and parses the creation time', () => {
    const subject = toNewestSubject(newest);

    expect(subject).toMatchObject({
      name: 'orders.created',
      format: 'json',
      latest: 2,
      versions: 3,
      awaitingApproval: 1,
    });
    expect(subject.createdAt.toISOString()).toBe('2026-10-01T09:00:00.000Z');
  });

  it('keeps an absent pointer absent', () => {
    expect(toNewestSubject({ ...newest, latest: null }).latest).toBeNull();
  });

  it('refuses a format and a lifecycle it does not know', () => {
    expect(() => toNewestSubject({ ...newest, format: 'thrift' })).toThrow(ConcordatError);
    expect(() => toNewestSubject({ ...newest, lifecycle: 'ARCHIVED' })).toThrow(ConcordatError);
  });
});

describe('toEnvironmentSummary', () => {
  it('maps every section', () => {
    const summary = toEnvironmentSummary(dto);

    expect(summary.subjects).toEqual(dto.subjects);
    expect(summary.versions).toEqual(dto.versions);
    expect(summary.newestSubjects).toHaveLength(1);
    expect(summary.breakingChanges[0]?.subject).toBe('orders.created');
    expect(summary.enforcement.map((t) => t.mode)).toEqual(['OFF', 'MONITOR', 'ENFORCE']);
    expect(summary.violations).toEqual({ distinct: 4, recent: 1, windowHours: 24 });
  });

  it('refuses an enforcement mode it does not know', () => {
    // A fourth mode would otherwise be drawn as a badge nobody has a tone or a tooltip for.
    expect(() =>
      toEnvironmentSummary({
        ...dto,
        enforcement: [{ mode: 'QUARANTINE', contracts: 1, publishBindings: 0, consumeBindings: 0 }],
      }),
    ).toThrow(ConcordatError);
  });
});
