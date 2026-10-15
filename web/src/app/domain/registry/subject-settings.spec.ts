import { describe, expect, it } from 'vitest';
import { COMPATIBILITY_MODES, COMPATIBILITY_SURFACES } from './compatibility';
import { OWNER_MAX_LENGTH } from './new-subject';
import {
  checkOwnerEdit,
  checkPolicyEdit,
  checkReviewersEdit,
  describePolicy,
  REVIEWERS_MAX,
} from './subject-settings';

const codes = (findings: readonly { severity: string; code: string }[]) =>
  findings.map((f) => `${f.severity}:${f.code}`);

describe('checkOwnerEdit', () => {
  it('sends a new owner, trimmed as the registry will store it', () => {
    expect(checkOwnerEdit('alice', 'bob')).toEqual({ send: 'bob', findings: [] });
    expect(checkOwnerEdit('alice', '  bob  ').send).toBe('bob');
  });

  it('trims with the registry’s set and not JavaScript’s', () => {
    // U+0085 is stripped by `System.String.Trim()` and not by `String.prototype.trim()`; U+FEFF is
    // the other way round. Built from code points: a literal in this file would be invisible.
    const nextLine = String.fromCharCode(0x85);
    const bom = String.fromCharCode(0xfeff);

    expect(checkOwnerEdit('alice', `${nextLine}bob${nextLine}`).send).toBe('bob');
    expect(checkOwnerEdit('alice', `${bom}bob`).send).toBe(`${bom}bob`);
  });

  describe('has nothing to send', () => {
    it.each([
      ['empty', ''],
      ['blank', '   '],
      ['only characters the registry strips', '\t\n' + String.fromCharCode(0x3000, 0x85)],
    ])('when it is %s, and says so as advice and not as a refusal', (_label, typed) => {
      // The registry answers 200 and changes nothing, so it is not a refusal: there is just no
      // request that means "blank".
      const edit = checkOwnerEdit('alice', typed);

      expect(edit.send).toBeNull();
      expect(codes(edit.findings)).toEqual(['advice:owner_blank']);
    });

    it('when it is the owner already shown, as advice that names the snapshot', () => {
      const edit = checkOwnerEdit('alice', '  alice ');

      expect(edit.send).toBeNull();
      expect(codes(edit.findings)).toEqual(['advice:owner_unchanged']);
      expect(edit.findings[0]?.message).toContain('as of the last time this page read');
    });
  });

  it('compares exactly: another case is another owner', () => {
    expect(checkOwnerEdit('alice', 'Alice').send).toBe('Alice');
  });

  it('refuses one over 256 characters with the registry’s code, counted after the trim', () => {
    const over = checkOwnerEdit('alice', 'o'.repeat(OWNER_MAX_LENGTH + 1));

    expect(over.send).toBeNull();
    expect(codes(over.findings)).toEqual(['refusal:actor_id_invalid']);
    expect(over.findings[0]?.message).toContain('257');

    expect(checkOwnerEdit('alice', ' ' + 'o'.repeat(OWNER_MAX_LENGTH) + ' ').send).toBe(
      'o'.repeat(OWNER_MAX_LENGTH),
    );
    expect(checkOwnerEdit('alice', ' ' + 'o'.repeat(OWNER_MAX_LENGTH + 1) + ' ').send).toBeNull();
  });

  it('places no rule on what an owner looks like', () => {
    for (const owner of ['alice@example.com', 'team-payments', 'a\nb', '日本語']) {
      expect(checkOwnerEdit('alice', owner).send).toBe(owner);
    }
  });
});

describe('checkPolicyEdit', () => {
  const pinned = { mode: 'BACKWARD', surface: 'WIRE_JSON' } as const;
  const inheriting = { mode: null, surface: null } as const;

  it('sends a whole policy', () => {
    expect(checkPolicyEdit(inheriting, { mode: 'FULL', surface: 'SOURCE' })).toEqual({
      send: { mode: 'FULL', surface: 'SOURCE' },
      findings: [],
    });
  });

  it('sends both members null to inherit, which is a request and not an absence', () => {
    expect(checkPolicyEdit(pinned, { mode: null, surface: null })).toEqual({
      send: { mode: null, surface: null },
      findings: [],
    });
  });

  it('refuses half a policy, as the registry does, in either direction', () => {
    const noSurface = checkPolicyEdit(inheriting, { mode: 'FULL', surface: null });
    const noMode = checkPolicyEdit(inheriting, { mode: null, surface: 'WIRE' });

    expect(noSurface.send).toBeNull();
    expect(codes(noSurface.findings)).toEqual(['refusal:invalid_request']);
    expect(noSurface.findings[0]?.message).toContain('surface');
    expect(noMode.send).toBeNull();
    expect(codes(noMode.findings)).toEqual(['refusal:invalid_request']);
    expect(noMode.findings[0]?.message).toContain('mode');
  });

  it('has nothing to send for the policy already shown, and for inheriting while inheriting', () => {
    const same = checkPolicyEdit(pinned, { mode: 'BACKWARD', surface: 'WIRE_JSON' });
    const sameInherit = checkPolicyEdit(inheriting, { mode: null, surface: null });

    expect(same.send).toBeNull();
    expect(codes(same.findings)).toEqual(['advice:policy_unchanged']);
    expect(sameInherit.send).toBeNull();
    expect(sameInherit.findings[0]?.message).toContain('no policy of its own');
  });

  it('treats pinning today’s value and inheriting as different requests', () => {
    // The point of `isInherited`: a subject that follows the default and one pinned to what the
    // default happens to be are not the same, and the form must let somebody go from one to the
    // other even though both read the same on a given day.
    const toPinned = checkPolicyEdit(inheriting, pinned);
    const toInherit = checkPolicyEdit(pinned, inheriting);

    expect(toPinned.send).toEqual(pinned);
    expect(toInherit.send).toEqual(inheriting);
  });

  it('accepts every mode with every surface', () => {
    for (const mode of COMPATIBILITY_MODES) {
      for (const surface of COMPATIBILITY_SURFACES) {
        const edit = checkPolicyEdit(inheriting, { mode, surface });

        expect(edit.send, `${mode} ${surface}`).toEqual({ mode, surface });
      }
    }
  });
});

describe('checkReviewersEdit', () => {
  it('sends one reviewer per line, trimmed, with blank lines dropped', () => {
    const edit = checkReviewersEdit([], '  alice@example.com \n\n\tbob@example.com\r\n   \n');

    expect(edit.send).toEqual(['alice@example.com', 'bob@example.com']);
    expect(edit.findings).toEqual([]);
  });

  it('does not split on a comma, because an actor id may hold one', () => {
    expect(checkReviewersEdit([], 'ci, prod').send).toEqual(['ci, prod']);
  });

  it('drops a repeat in any case and keeps the first spelling, as the registry does', () => {
    const edit = checkReviewersEdit([], 'Alice@Example.com\nbob@example.com\nALICE@example.com');

    expect(edit.send).toEqual(['Alice@Example.com', 'bob@example.com']);
  });

  it('keeps the order typed, because the order is what the page shows', () => {
    expect(checkReviewersEdit([], 'b@x\na@x').send).toEqual(['b@x', 'a@x']);
  });

  it('trims what the registry trims and not what JavaScript does', () => {
    // U+0085 and U+200B differ between the two (see registry-text.ts); the registry's rule wins.
    expect(checkReviewersEdit([], '\u00a0alice@example.com\u00a0').send).toEqual([
      'alice@example.com',
    ]);
  });

  describe('a draft that is not a change', () => {
    it('is advice and sends nothing, for the set already held', () => {
      const edit = checkReviewersEdit(['a@x', 'b@x'], 'a@x\nb@x');

      expect(edit.send).toBeNull();
      expect(edit.findings).toEqual([
        expect.objectContaining({
          field: 'reviewers',
          severity: 'advice',
          code: 'reviewers_unchanged',
        }),
      ]);
    });

    it('says nobody is named, for an empty draft over an empty set', () => {
      const edit = checkReviewersEdit([], '  \n ');

      expect(edit.send).toBeNull();
      expect(edit.findings[0]?.message).toContain('No reviewers are named');
    });

    it('treats the same names in another order as a change', () => {
      expect(checkReviewersEdit(['a@x', 'b@x'], 'b@x\na@x').send).toEqual(['b@x', 'a@x']);
    });

    it('treats the same names in another case as a change, because the case is what is stored', () => {
      expect(checkReviewersEdit(['alice@example.com'], 'Alice@example.com').send).toEqual([
        'Alice@example.com',
      ]);
    });
  });

  it('sends an empty list to clear a set that is named', () => {
    const edit = checkReviewersEdit(['a@x'], '');

    expect(edit.send).toEqual([]);
    expect(edit.findings).toEqual([]);
  });

  describe('what the registry refuses', () => {
    it('refuses a reviewer over 256 characters, counted after the trim', () => {
      expect(checkReviewersEdit([], 'r'.repeat(257)).send).toBeNull();
      expect(checkReviewersEdit([], `  ${'r'.repeat(256)}  `).send).toEqual(['r'.repeat(256)]);
    });

    it('names the registry’s own code for it', () => {
      expect(checkReviewersEdit([], 'r'.repeat(257)).findings[0]).toEqual(
        expect.objectContaining({ severity: 'refusal', code: 'actor_id_invalid' }),
      );
    });

    it('accepts twenty and refuses twenty-one distinct', () => {
      const names = (n: number) => Array.from({ length: n }, (_, i) => `r${i}@x`).join('\n');

      expect(checkReviewersEdit([], names(REVIEWERS_MAX)).send).toHaveLength(REVIEWERS_MAX);
      expect(checkReviewersEdit([], names(REVIEWERS_MAX + 1)).send).toBeNull();
      expect(checkReviewersEdit([], names(REVIEWERS_MAX + 1)).findings[0]).toEqual(
        expect.objectContaining({ severity: 'refusal', code: 'reviewers_invalid' }),
      );
    });

    it('counts distinct reviewers, so a long run of repeats is not too many', () => {
      const repeated = Array.from({ length: 50 }, () => 'alice@example.com').join('\n');

      expect(checkReviewersEdit([], repeated).send).toEqual(['alice@example.com']);
    });
  });

  describe('locking yourself out', () => {
    it('is said, as advice, when the set leaves out the signed-in person', () => {
      const edit = checkReviewersEdit([], 'alice@example.com', 'carol@example.com');

      expect(edit.send).toEqual(['alice@example.com']);
      expect(edit.findings).toEqual([
        expect.objectContaining({ severity: 'advice', code: 'reviewers_exclude_you' }),
      ]);
    });

    it('is not said when they are named, whatever the case', () => {
      expect(checkReviewersEdit([], 'Carol@Example.com', 'carol@example.com').findings).toEqual([]);
    });

    it('is not said for clearing, which lets everyone decide', () => {
      expect(checkReviewersEdit(['a@x'], '', 'carol@example.com').findings).toEqual([]);
    });

    it('is not said when nobody is known to be signed in', () => {
      expect(checkReviewersEdit([], 'alice@example.com', null).findings).toEqual([]);
    });
  });
});
describe('describePolicy', () => {
  it('reads a policy as mode and surface, and an absent one as inherited', () => {
    expect(describePolicy({ mode: 'FULL_TRANSITIVE', surface: 'WIRE_JSON' })).toBe(
      'FULL_TRANSITIVE · WIRE_JSON',
    );
    expect(describePolicy({ mode: null, surface: null })).toBe('Inherited');
  });
});
