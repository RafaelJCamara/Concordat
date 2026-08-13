import { describe, expect, it } from 'vitest';
import { toDivergence, toPolicy, toSubject, type SubjectDto } from './subject-dtos';

// The mapping is where a wire change shows up first, and where it is cheapest to catch.
// These cases are the ones that are not obvious from reading the DTO types.

const subject: SubjectDto = {
  name: 'orders.created',
  format: 'json',
  owner: 'orders-team',
  lifecycle: 'ACTIVE',
  contentModel: 'OPEN',
  compatibilityPolicy: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
  latest: 1,
  versions: [
    {
      ordinal: 1,
      schemaId: '0123456789abcdef0123456789abcdef',
      semanticVersion: '1.0.0',
      status: 'ACTIVE',
      changelog: null,
      registeredAt: '2026-08-13T09:00:00+00:00',
      registeredBy: 'ci',
      deprecated: false,
    },
    {
      ordinal: 2,
      schemaId: 'fedcba9876543210fedcba9876543210',
      semanticVersion: '2.0.0',
      status: 'AWAITING_APPROVAL',
      changelog: 'drops customerId',
      registeredAt: '2026-08-13T10:00:00+00:00',
      registeredBy: 'ci',
      deprecated: false,
    },
  ],
};

describe('toSubject', () => {
  it('keeps latest pointing at the approved version, not the highest ordinal', () => {
    // The whole of ADR-017 in one assertion: ordinal 2 exists and is newer, and `latest`
    // must still be 1 because 2 has not been approved.
    expect(toSubject(subject).latest).toBe(1);
  });

  it('parses timestamps into Dates', () => {
    const [first] = toSubject(subject).versions;

    expect(first?.registeredAt.toISOString()).toBe('2026-08-13T09:00:00.000Z');
  });

  it('refuses a token it does not recognise rather than guessing', () => {
    const unknown: SubjectDto = {
      ...subject,
      versions: [{ ...subject.versions[0]!, status: 'SUPERSEDED' }],
    };

    expect(() => toSubject(unknown)).toThrowError(/does not recognise/);
  });
});

describe('toPolicy', () => {
  it('preserves inheriting as a state of its own', () => {
    expect(toPolicy({ mode: null, surface: null })).toEqual({ mode: null, surface: null });
  });
});

describe('surface spelling', () => {
  it('reads one spelling from both shapes that carry a surface', () => {
    // Until M6.1 a policy said WIREJSON and a breaking change said WIRE_JSON for the same
    // value. Both now say WIRE_JSON, and this pins that they agree.
    const fromPolicy = toPolicy({ mode: 'BACKWARD', surface: 'WIRE_JSON' });
    const fromDivergence = toDivergence({
      path: '/properties/customerId',
      kind: 'property_removed',
      direction: 'BACKWARD',
      surface: 'WIRE_JSON',
      message: 'customerId was removed.',
      conflictsWithVersion: 1,
    });

    expect(fromPolicy.surface).toBe('WIRE_JSON');
    expect(fromDivergence.surface).toBe(fromPolicy.surface);
  });

  it('refuses the pre-M6.1 spelling rather than quietly accepting it', () => {
    // The leniency is gone on purpose: accepting both would mean a future divergence in the
    // protocol passed silently, which is exactly how this one survived five milestones.
    expect(() => toPolicy({ mode: 'BACKWARD', surface: 'WIREJSON' })).toThrow();
  });
});
