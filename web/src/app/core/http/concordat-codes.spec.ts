import { describe, expect, it } from 'vitest';
import { DOMAIN_CONCORDAT_CODES } from './concordat-codes.generated';
import { TRANSPORT_CONCORDAT_CODES, isKnownConcordatCode } from './concordat-codes';

// The code catalogue is protocol (ADR-019), and this app assembles it from two lists that
// are maintained in different places and by different means. The tests that matter here are
// about the seam between them, not about the guard's one-line body.

describe('isKnownConcordatCode', () => {
  it('recognises a code from the generated domain catalogue', () => {
    expect(isKnownConcordatCode('subject_not_found')).toBe(true);
  });

  it('recognises every transport code', () => {
    // These are the ones no generator can produce, so nothing else checks that the list and
    // the guard agree.
    for (const code of TRANSPORT_CONCORDAT_CODES) {
      expect(isKnownConcordatCode(code)).toBe(true);
    }
  });

  it('recognises the two codes this app raises itself', () => {
    // `registry_unreachable` and `registry_refused` are synthesised by `toConcordatError`.
    // If the guard did not know them, an error the app made up would be reported as one the
    // registry sent from the future.
    expect(isKnownConcordatCode('registry_unreachable')).toBe(true);
    expect(isKnownConcordatCode('registry_refused')).toBe(true);
  });

  it('does not recognise a code from a newer registry', () => {
    expect(isKnownConcordatCode('subject_frozen')).toBe(false);
  });

  it('is not fooled by inherited object members', () => {
    // A `Set` rather than an object literal, precisely so that `'toString'` and
    // `'constructor'` are not silently "known" codes.
    expect(isKnownConcordatCode('toString')).toBe(false);
    expect(isKnownConcordatCode('constructor')).toBe(false);
  });
});

describe('the two catalogues', () => {
  it('do not overlap', () => {
    // NOTES-FOR-INTEGRATION §3.4: `insufficient_scope` and `invalid_request` are declared
    // client-side because the .NET catalogue does not have them yet. When M8 adds them, the
    // union would carry each twice and the hand-written entry would need deleting. This is
    // the test that says so at the moment it happens, rather than a comment nobody reads.
    const domain = new Set<string>(DOMAIN_CONCORDAT_CODES);
    const duplicated = TRANSPORT_CONCORDAT_CODES.filter((code) => domain.has(code));

    expect(duplicated).toEqual([]);
  });

  it('together cover the codes the UI names by hand', () => {
    // Each of these is spelled out somewhere in the app or in DESIGN. A rename in
    // `ConcordatCodes.cs` that `npm run codes:generate` faithfully carries across would
    // otherwise leave the UI branching on a string nothing can ever equal.
    for (const code of [
      'subject_not_found',
      'subject_already_exists',
      'subject_retired',
      'verdict_policy_mismatch',
      'version_not_awaiting_approval',
      'insufficient_scope',
      'invalid_request',
    ]) {
      expect(isKnownConcordatCode(code)).toBe(true);
    }
  });
});

describe('the generated domain catalogue', () => {
  it('is not empty', () => {
    // A generator that silently produced nothing would leave every domain code looking
    // unknown, and `npm run codes:check` only runs where somebody remembers to run it.
    expect(DOMAIN_CONCORDAT_CODES.length).toBeGreaterThan(0);
  });

  it('has no duplicates', () => {
    expect(new Set(DOMAIN_CONCORDAT_CODES).size).toBe(DOMAIN_CONCORDAT_CODES.length);
  });

  it('is spelled in snake_case throughout', () => {
    // The wire spelling, not the C# member name. A generator that started emitting
    // `SubjectNotFound` would produce a catalogue that compiles and matches nothing.
    for (const code of DOMAIN_CONCORDAT_CODES) {
      expect(code).toMatch(/^[a-z][a-z0-9]*(_[a-z0-9]+)*$/);
    }
  });
});
