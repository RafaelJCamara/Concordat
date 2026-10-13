import type { SchemaVersion } from './subject';

/**
 * A `MAJOR.MINOR.PATCH[-pre-release]` label, as the registry reads one (ADR-004).
 *
 * <b>A port of `SemanticVersion.cs`, not a use of a semver package.</b> The registry's grammar
 * is narrower than SemVer 2.0.0 in one place that matters — build metadata (`1.0.0+a`) is
 * refused, because two labels differing only in metadata compare equal and "each label must
 * increase" would then accept a label that did not — and a general-purpose library accepts
 * it. A client that agreed with the library would offer a label the registry refuses, which
 * is the one disagreement this file exists to prevent.
 */
export interface SemanticVersion {
  readonly major: number;
  readonly minor: number;
  readonly patch: number;
  /** The identifiers after the hyphen, or null for an ordinary release. */
  readonly preRelease: string | null;
}

/** `Int32.MaxValue`: the registry parses each component into an `int` and refuses overflow. */
const INT32_MAX = 2_147_483_647;

const CORE = /^(\d+)\.(\d+)\.(\d+)(.*)$/;

// The registry's pre-release grammar, character for character: dot-separated identifiers of
// ASCII alphanumerics and hyphens, and a numeric identifier may not carry a leading zero
// (`01` and `1` would be different strings that compare equal).
const PRE_RELEASE =
  /^(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*$/;

/** What went wrong with a label, in the registry's own terms. */
export type SemanticVersionProblem =
  | { readonly kind: 'invalid'; readonly message: string }
  | { readonly kind: 'build_metadata'; readonly message: string };

/** The outcome of reading a label. */
export type SemanticVersionParse =
  | { readonly ok: true; readonly value: SemanticVersion }
  | { readonly ok: false; readonly problem: SemanticVersionProblem };

/**
 * Reads a label.
 *
 * Mirrors `SemanticVersion.Create`, including its ordering of checks: the core triple is
 * matched first and *then* what follows is inspected, so `-1.0.0` is reported as malformed
 * rather than as an unsupported pre-release, which would send the author looking in the wrong
 * place. Whether a *pre-release* is acceptable is a property of the environment, not of the
 * string, and is not decided here.
 */
export function parseSemanticVersion(text: string): SemanticVersionParse {
  const trimmed = text.trim();

  if (trimmed === '') {
    return invalid('A semantic version label is required.');
  }

  const match = CORE.exec(trimmed);

  if (match === null) {
    return invalid(`Expected MAJOR.MINOR.PATCH; got '${trimmed}'.`);
  }

  const rest = match[4] ?? '';
  let preRelease: string | null = null;

  if (rest.length > 0) {
    if (rest.startsWith('+')) {
      return buildMetadata(trimmed);
    }

    if (!rest.startsWith('-')) {
      return invalid(`Unexpected trailing text '${rest}' in '${trimmed}'.`);
    }

    preRelease = rest.slice(1);

    if (preRelease.includes('+')) {
      return buildMetadata(trimmed);
    }

    if (!PRE_RELEASE.test(preRelease)) {
      return invalid(
        `'${preRelease}' is not a valid pre-release: dot-separated alphanumerics and hyphens, ` +
          `no leading zero on a numeric identifier, in '${trimmed}'.`,
      );
    }
  }

  const texts = [match[1] ?? '', match[2] ?? '', match[3] ?? ''];
  const numbers = texts.map(Number);

  // The pattern permits digit runs that overflow Int32, and the registry refuses them there
  // rather than in the grammar. `Number` of a long run is not an integer we can trust.
  const overflow = numbers.findIndex((n) => !Number.isSafeInteger(n) || n > INT32_MAX);

  if (overflow !== -1) {
    return invalid(`'${texts[overflow]}' is out of range, in '${trimmed}'.`);
  }

  return {
    ok: true,
    value: { major: numbers[0] ?? 0, minor: numbers[1] ?? 0, patch: numbers[2] ?? 0, preRelease },
  };
}

function invalid(message: string): SemanticVersionParse {
  return { ok: false, problem: { kind: 'invalid', message } };
}

function buildMetadata(label: string): SemanticVersionParse {
  return {
    ok: false,
    problem: {
      kind: 'build_metadata',
      message:
        `Build metadata is not supported; got '${label}'. SemVer ignores it for precedence, so ` +
        'two labels carrying different metadata compare equal, and this registry requires each ' +
        'label to increase on the last.',
    },
  };
}

/** Writes a label the way the registry would read it back. */
export function formatSemanticVersion(version: SemanticVersion): string {
  const core = `${version.major}.${version.minor}.${version.patch}`;

  return version.preRelease === null ? core : `${core}-${version.preRelease}`;
}

/** Orders two labels by SemVer 2.0.0 precedence; negative when `left` is earlier. */
export function compareSemanticVersions(left: SemanticVersion, right: SemanticVersion): number {
  return (
    left.major - right.major ||
    left.minor - right.minor ||
    left.patch - right.patch ||
    comparePreRelease(left.preRelease, right.preRelease)
  );
}

function comparePreRelease(left: string | null, right: string | null): number {
  // Absent beats present: 1.0.0 follows 1.0.0-rc.1. The one place SemVer's ordering is
  // counter-intuitive if read as plain string comparison.
  if (left === null || right === null) {
    return left === right ? 0 : left === null ? 1 : -1;
  }

  const mine = left.split('.');
  const theirs = right.split('.');

  for (let i = 0; i < Math.min(mine.length, theirs.length); i++) {
    const a = mine[i] ?? '';
    const b = theirs[i] ?? '';
    const aNumeric = isNumericIdentifier(a);
    const bNumeric = isNumericIdentifier(b);

    // Numeric identifiers compare numerically, so rc.10 follows rc.9 — which plain string
    // ordering gets backwards, and which is exactly the sequence a release-candidate
    // pipeline produces.
    if (aNumeric && bNumeric) {
      const numeric = Number(a) - Number(b);

      if (numeric !== 0) {
        return numeric;
      }

      continue;
    }

    // A numeric identifier always precedes an alphanumeric one.
    if (aNumeric !== bNumeric) {
      return aNumeric ? -1 : 1;
    }

    // `string.CompareOrdinal` compares UTF-16 code units, which is what `<` does here. The
    // grammar is ASCII-only, so the two agree.
    if (a !== b) {
      return a < b ? -1 : 1;
    }
  }

  // Everything shared is equal, so more identifiers wins: 1.0.0-rc.1.1 follows 1.0.0-rc.1.
  return mine.length - theirs.length;
}

/** An identifier the registry's `int.TryParse` would take: digits that fit an Int32. */
function isNumericIdentifier(identifier: string): boolean {
  return /^\d+$/.test(identifier) && Number(identifier) <= INT32_MAX;
}

/**
 * The label a new registration has to increase on.
 *
 * <b>The highest label among versions that are active or awaiting approval — not the label on
 * `latest`.</b> That is `Subject.VerifySemanticVersion`'s own rule, and the difference is the
 * whole reason this function exists: with v1 `1.0.0` current and v2 `2.0.0` held at the gate,
 * `latest` says 1.0.0 and the registry says 2.0.0, so a suggestion seeded from the pointer
 * would offer `1.0.1` and be refused as `semver_not_increasing`. Rejected and dismissed
 * versions are withdrawn claims and do not count; a pending one still does, because it is
 * still being asked for.
 *
 * Null when no live version carries a label, or none parses — the registry would likewise
 * have nothing to compare against.
 */
export function highestLiveLabel(versions: readonly SchemaVersion[]): SemanticVersion | null {
  let highest: SemanticVersion | null = null;

  for (const version of versions) {
    if (version.semanticVersion === null) {
      continue;
    }

    if (version.status !== 'ACTIVE' && version.status !== 'AWAITING_APPROVAL') {
      continue;
    }

    const parsed = parseSemanticVersion(version.semanticVersion);

    if (parsed.ok && (highest === null || compareSemanticVersions(parsed.value, highest) > 0)) {
      highest = parsed.value;
    }
  }

  return highest;
}

/** The three ways to move a label up. */
export interface LabelSuggestions {
  readonly patch: SemanticVersion;
  readonly minor: SemanticVersion;
  readonly major: SemanticVersion;
}

/**
 * What each kind of bump would produce from a label.
 *
 * A pre-release is bumped *to its own release* by `patch`: `2.0.0-rc.1` precedes `2.0.0`, so
 * the smallest label that increases on it is the release itself. Adding one to the patch
 * component would skip a number nobody asked to skip.
 */
export function suggestLabels(from: SemanticVersion): LabelSuggestions {
  const release = (major: number, minor: number, patch: number): SemanticVersion => ({
    major,
    minor,
    patch,
    preRelease: null,
  });

  return {
    patch:
      from.preRelease === null
        ? release(from.major, from.minor, from.patch + 1)
        : release(from.major, from.minor, from.patch),
    minor: release(from.major, from.minor + 1, 0),
    major: release(from.major + 1, 0, 0),
  };
}
