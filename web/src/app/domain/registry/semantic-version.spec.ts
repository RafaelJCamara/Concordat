import { describe, expect, it } from 'vitest';
import {
  compareSemanticVersions,
  formatSemanticVersion,
  highestLiveLabel,
  parseSemanticVersion,
  suggestLabels,
  type SemanticVersion,
} from './semantic-version';
import type { SchemaVersion } from './subject';

// This is a port of a C# value object, and the failure it guards against is a disagreement
// with that one: a label this file accepts and the registry refuses, or the reverse. Each
// case below is a rule `SemanticVersion.cs` states, not one invented here.

function read(text: string): SemanticVersion {
  const parsed = parseSemanticVersion(text);

  if (!parsed.ok) {
    throw new Error(`expected '${text}' to parse: ${parsed.problem.message}`);
  }

  return parsed.value;
}

function version(
  ordinal: number,
  semanticVersion: string | null,
  status: SchemaVersion['status'] = 'ACTIVE',
): SchemaVersion {
  return {
    ordinal,
    schemaId: `${ordinal}`.padStart(32, '0'),
    semanticVersion,
    status,
    changelog: null,
    registeredAt: new Date('2026-10-13T09:00:00Z'),
    registeredBy: 'ci',
    deprecated: false,
  };
}

describe('parseSemanticVersion', () => {
  it('reads the three components and trims the text first', () => {
    expect(read('  1.2.3  ')).toEqual({ major: 1, minor: 2, patch: 3, preRelease: null });
  });

  it.each(['', '   ', '1.2', '1.2.3.4', 'v1.2.3', '-1.0.0', '1.0.0x', '1.0.0-', '1.0.0-01'])(
    'refuses %j as malformed',
    (text) => {
      const parsed = parseSemanticVersion(text);

      expect(parsed.ok).toBe(false);
      expect(!parsed.ok && parsed.problem.kind).toBe('invalid');
    },
  );

  it('names build metadata as its own problem, not as a malformed label', () => {
    // SemVer ignores it for precedence, so `1.0.0+a` and `1.0.0+b` compare equal while being
    // different strings; "each label must increase" would then accept one that did not.
    for (const text of ['1.0.0+build', '1.0.0-rc.1+build']) {
      const parsed = parseSemanticVersion(text);

      expect(!parsed.ok && parsed.problem.kind).toBe('build_metadata');
    }
  });

  it('refuses a component the registry cannot hold in an Int32', () => {
    // The grammar admits the digits; the registry's `int.TryParse` is what refuses them.
    expect(parseSemanticVersion('2147483647.0.0').ok).toBe(true);
    expect(parseSemanticVersion('2147483648.0.0').ok).toBe(false);
    expect(parseSemanticVersion('1.0.99999999999999999999').ok).toBe(false);
  });

  it('accepts a pre-release and keeps it', () => {
    expect(read('2.0.0-rc.1').preRelease).toBe('rc.1');
    expect(read('2.0.0-0.3.7').preRelease).toBe('0.3.7');
    expect(read('2.0.0-x-y-z.--').preRelease).toBe('x-y-z.--');
  });
});

describe('compareSemanticVersions', () => {
  const order = (a: string, b: string) => Math.sign(compareSemanticVersions(read(a), read(b)));

  it('orders by component, not by string', () => {
    expect(order('1.2.0', '1.10.0')).toBe(-1);
    expect(order('2.0.0', '10.0.0')).toBe(-1);
  });

  it('puts a pre-release before its own release', () => {
    // The rule that makes rc.1, rc.2, then the release the sequence a pipeline produces.
    expect(order('1.0.0-rc.1', '1.0.0')).toBe(-1);
    expect(order('1.0.0', '1.0.0-rc.1')).toBe(1);
  });

  it('compares numeric identifiers numerically and alphanumeric ones in ordinal order', () => {
    expect(order('1.0.0-rc.9', '1.0.0-rc.10')).toBe(-1);
    expect(order('1.0.0-alpha', '1.0.0-beta')).toBe(-1);
    // Uppercase precedes lowercase in ASCII, which is `string.CompareOrdinal`'s answer too.
    expect(order('1.0.0-B', '1.0.0-a')).toBe(-1);
  });

  it('puts a numeric identifier before an alphanumeric one, and more identifiers last', () => {
    expect(order('1.0.0-1', '1.0.0-alpha')).toBe(-1);
    expect(order('1.0.0-rc.1', '1.0.0-rc.1.1')).toBe(-1);
  });

  it('treats a numeric identifier too large for an Int32 as alphanumeric, as the registry does', () => {
    // `int.TryParse` fails on it, so it is compared as text against a numeric one.
    expect(order('1.0.0-1', '1.0.0-99999999999')).toBe(-1);
  });
});

describe('highestLiveLabel', () => {
  it('is the highest label among live versions, not the one on `latest`', () => {
    // The reason this function exists. v1 is current at 1.0.0 and v2 is held at 2.0.0: the
    // pointer says 1.0.0, the registry says 2.0.0, and a suggestion seeded from the pointer
    // would offer 1.0.1 and be refused as `semver_not_increasing`.
    const highest = highestLiveLabel([
      version(1, '1.0.0'),
      version(2, '2.0.0', 'AWAITING_APPROVAL'),
    ]);

    expect(highest && formatSemanticVersion(highest)).toBe('2.0.0');
  });

  it('ignores rejected and dismissed versions, which are withdrawn claims', () => {
    const highest = highestLiveLabel([
      version(1, '1.0.0'),
      version(2, '3.0.0', 'REJECTED'),
      version(3, '4.0.0', 'DISMISSED'),
    ]);

    expect(highest && formatSemanticVersion(highest)).toBe('1.0.0');
  });

  it('is null when nothing live carries a label', () => {
    expect(highestLiveLabel([])).toBeNull();
    expect(highestLiveLabel([version(1, null), version(2, '9.0.0', 'REJECTED')])).toBeNull();
  });

  it('skips a stored label that does not parse rather than failing the screen', () => {
    // A registry that learns a looser grammar than this build should not blank the form.
    const highest = highestLiveLabel([version(1, 'not-a-label'), version(2, '1.1.0')]);

    expect(highest && formatSemanticVersion(highest)).toBe('1.1.0');
  });
});

describe('suggestLabels', () => {
  it('offers each bump from a release', () => {
    const { patch, minor, major } = suggestLabels(read('1.4.2'));

    expect([patch, minor, major].map(formatSemanticVersion)).toEqual(['1.4.3', '1.5.0', '2.0.0']);
  });

  it('bumps a pre-release to its own release rather than skipping a patch number', () => {
    // 2.0.0-rc.1 precedes 2.0.0, so 2.0.0 is the smallest label that increases on it.
    const { patch } = suggestLabels(read('2.0.0-rc.1'));

    expect(formatSemanticVersion(patch)).toBe('2.0.0');
  });

  it('only ever suggests labels that increase on the one they came from', () => {
    for (const text of ['0.0.1', '1.0.0', '1.0.0-rc.9', '3.2.1']) {
      const from = read(text);
      const { patch, minor, major } = suggestLabels(from);

      for (const next of [patch, minor, major]) {
        expect(compareSemanticVersions(next, from)).toBeGreaterThan(0);
      }
    }
  });
});
