import { describe, expect, it } from 'vitest';
import { COMPATIBILITY_MODES, COMPATIBILITY_SURFACES } from './compatibility';
import { OWNER_MAX_LENGTH } from './new-subject';
import { checkOwnerEdit, checkPolicyEdit, describePolicy } from './subject-settings';

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

describe('describePolicy', () => {
  it('reads a policy as mode and surface, and an absent one as inherited', () => {
    expect(describePolicy({ mode: 'FULL_TRANSITIVE', surface: 'WIRE_JSON' })).toBe(
      'FULL_TRANSITIVE · WIRE_JSON',
    );
    expect(describePolicy({ mode: null, surface: null })).toBe('Inherited');
  });
});
