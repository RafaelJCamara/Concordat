import { describe, expect, it } from 'vitest';
import { latestVersion, pendingVersions, type SchemaVersion, type Subject } from './subject';

// Two helpers, and both of them are places where "obvious" is wrong. `latest` is not the
// highest ordinal, and the pending list is not the tail of the versions array.

function version(overrides: Partial<SchemaVersion> & { ordinal: number }): SchemaVersion {
  return {
    schemaId: `${overrides.ordinal}`.padStart(32, '0'),
    semanticVersion: null,
    status: 'ACTIVE',
    changelog: null,
    registeredAt: new Date('2026-08-13T09:00:00Z'),
    registeredBy: 'ci',
    deprecated: false,
    ...overrides,
  };
}

function subject(overrides: Partial<Subject> = {}): Subject {
  return {
    name: 'orders.created',
    format: 'json',
    owner: 'orders-team',
    lifecycle: 'ACTIVE',
    contentModel: 'OPEN',
    compatibilityPolicy: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
    latest: 1,
    versions: [version({ ordinal: 1 })],
    ...overrides,
  };
}

describe('latestVersion', () => {
  it('follows the pointer rather than the highest ordinal', () => {
    // The whole of ADR-017. Ordinal 2 exists, is newer, and is held at the approval gate;
    // resolving `latest` by sorting would publish it to every consumer reading the list.
    const held = subject({
      latest: 1,
      versions: [version({ ordinal: 1 }), version({ ordinal: 2, status: 'AWAITING_APPROVAL' })],
    });

    expect(latestVersion(held)?.ordinal).toBe(1);
  });

  it('is null when nothing is active yet', () => {
    // The ordinary state right after a subject is created, not an error.
    expect(latestVersion(subject({ latest: null, versions: [] }))).toBeNull();
  });

  it('is null when the pointer names a version the payload does not carry', () => {
    // A list endpoint that trims versions, or a response mid-migration. Returning null
    // makes the row render an em dash; throwing here would take down the whole list, and
    // returning the nearest version would quietly show the wrong contract.
    const trimmed = subject({ latest: 7, versions: [version({ ordinal: 1 })] });

    expect(latestVersion(trimmed)).toBeNull();
  });
});

describe('pendingVersions', () => {
  it('counts only the versions actually held at the gate', () => {
    const s = subject({
      latest: 1,
      versions: [
        version({ ordinal: 1 }),
        version({ ordinal: 2, status: 'AWAITING_APPROVAL' }),
        version({ ordinal: 3, status: 'REJECTED' }),
        version({ ordinal: 4, status: 'AWAITING_APPROVAL' }),
      ],
    });

    expect(pendingVersions(s).map((v) => v.ordinal)).toEqual([4, 2]);
  });

  it('puts the newest first', () => {
    // Reviewers work the newest request first, and the badge on the subject list is a
    // shortcut to it.
    const s = subject({
      versions: [
        version({ ordinal: 5, status: 'AWAITING_APPROVAL' }),
        version({ ordinal: 9, status: 'AWAITING_APPROVAL' }),
      ],
    });

    expect(pendingVersions(s)[0]?.ordinal).toBe(9);
  });

  it('leaves the caller’s own version order alone', () => {
    // `Array.prototype.sort` mutates. It is safe here only because `filter` has already
    // made a copy, and dropping the filter — or reordering the two calls — would silently
    // reorder the array the caller is rendering from.
    const s = subject({
      versions: [
        version({ ordinal: 1, status: 'AWAITING_APPROVAL' }),
        version({ ordinal: 2, status: 'AWAITING_APPROVAL' }),
      ],
    });

    pendingVersions(s);

    expect(s.versions.map((v) => v.ordinal)).toEqual([1, 2]);
  });

  it('is empty when nothing is waiting', () => {
    expect(pendingVersions(subject())).toEqual([]);
  });
});
