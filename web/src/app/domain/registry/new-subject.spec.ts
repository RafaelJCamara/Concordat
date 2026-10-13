import { describe, expect, it } from 'vitest';
import {
  OWNER_MAX_LENGTH,
  checkNewSubject,
  toNewSubject,
  type NewSubjectDraft,
} from './new-subject';

const draft = (overrides: Partial<NewSubjectDraft> = {}): NewSubjectDraft => ({
  name: 'acme.orders.OrderCreated',
  format: 'json',
  owner: 'alice',
  contentModel: 'OPEN',
  mode: null,
  surface: null,
  ...overrides,
});

const codes = (d: NewSubjectDraft, existing: readonly string[] = []) =>
  checkNewSubject(d, existing).map((f) => `${f.field}:${f.severity}:${f.code}`);

describe('checkNewSubject', () => {
  it('finds nothing wrong with an ordinary subject', () => {
    expect(codes(draft())).toEqual([]);
  });

  describe('the name', () => {
    it('refuses it with the registry’s own code', () => {
      expect(codes(draft({ name: 'order-created' }))).toEqual([
        'name:refusal:subject_name_invalid',
      ]);
      expect(codes(draft({ name: '' }))).toEqual(['name:refusal:subject_name_invalid']);
    });

    it('is judged after the registry’s own trim', () => {
      expect(codes(draft({ name: '  acme.orders  ' }))).toEqual([]);
    });
  });

  describe('the owner', () => {
    it.each([
      ['empty', ''],
      ['blank', '   '],
    ])('refuses it when %s', (_label, owner) => {
      expect(codes(draft({ owner }))).toEqual(['owner:refusal:actor_id_invalid']);
    });

    it('accepts exactly 256 characters, after trimming, and refuses 257', () => {
      expect(codes(draft({ owner: ' ' + 'o'.repeat(OWNER_MAX_LENGTH) + ' ' }))).toEqual([]);
      expect(codes(draft({ owner: 'o'.repeat(OWNER_MAX_LENGTH + 1) }))).toEqual([
        'owner:refusal:actor_id_invalid',
      ]);
    });

    it('places no rule on what the owner looks like', () => {
      // The registry takes an email, a team name or a line break; so does this.
      for (const owner of ['alice@example.com', 'team-payments', 'a\nb', '日本語']) {
        expect(codes(draft({ owner }))).toEqual([]);
      }
    });
  });

  describe('the compatibility policy', () => {
    it('is fine absent, which means inherit', () => {
      expect(codes(draft({ mode: null, surface: null }))).toEqual([]);
    });

    it('is fine when whole', () => {
      expect(codes(draft({ mode: 'FULL_TRANSITIVE', surface: 'SOURCE' }))).toEqual([]);
    });

    it('is refused when only one half is chosen, which the registry answers 400', () => {
      expect(codes(draft({ mode: 'BACKWARD', surface: null }))).toEqual([
        'compatibility:refusal:invalid_request',
      ]);
      expect(codes(draft({ mode: null, surface: 'WIRE' }))).toEqual([
        'compatibility:refusal:invalid_request',
      ]);
    });

    it('says which half is missing', () => {
      const [mode] = checkNewSubject(draft({ mode: 'BACKWARD' }), []);
      const [surface] = checkNewSubject(draft({ surface: 'WIRE' }), []);

      expect(mode?.message).toContain('surface');
      expect(surface?.message).toContain('mode');
    });
  });

  describe('a name that is already there', () => {
    it('is advice, never a refusal, because the list it came from is a snapshot', () => {
      const findings = checkNewSubject(draft(), ['acme.orders.OrderCreated']);

      expect(findings.map((f) => [f.severity, f.code])).toEqual([['advice', 'subject_exists']]);
    });

    it('compares the name after the registry’s trim', () => {
      expect(
        codes(draft({ name: ' acme.orders.OrderCreated ' }), ['acme.orders.OrderCreated']),
      ).toEqual(['name:advice:subject_exists']);
    });

    it('notices a name that differs only in case, because the registry will not', () => {
      const findings = checkNewSubject(draft({ name: 'acme.orders.ordercreated' }), [
        'acme.orders.OrderCreated',
      ]);

      expect(findings.map((f) => f.code)).toEqual(['subject_differs_in_case']);
      expect(findings[0]?.message).toContain('acme.orders.OrderCreated');
      expect(findings[0]?.message).toContain('separate subject');
    });

    it('says nothing about a name nothing resembles', () => {
      expect(codes(draft(), ['acme.orders.OrderShipped', 'other.Thing'])).toEqual([]);
    });

    it('says nothing about a name that is not valid, which is the louder problem', () => {
      expect(codes(draft({ name: 'a b' }), ['a b'])).toEqual(['name:refusal:subject_name_invalid']);
    });
  });
});

describe('toNewSubject', () => {
  it('sends the trimmed name and owner', () => {
    expect(toNewSubject(draft({ name: ' acme.orders ', owner: ' alice ' }))).toEqual({
      name: 'acme.orders',
      format: 'json',
      owner: 'alice',
      contentModel: 'OPEN',
      compatibility: null,
    });
  });

  it('sends a whole policy, and null to inherit', () => {
    expect(toNewSubject(draft({ mode: 'FULL', surface: 'WIRE_JSON' }))?.compatibility).toEqual({
      mode: 'FULL',
      surface: 'WIRE_JSON',
    });
    expect(toNewSubject(draft())?.compatibility).toBeNull();
  });

  it('is null while the registry would certainly refuse it', () => {
    expect(toNewSubject(draft({ name: 'no spaces' }))).toBeNull();
    expect(toNewSubject(draft({ owner: '' }))).toBeNull();
    expect(toNewSubject(draft({ mode: 'FULL' }))).toBeNull();
  });

  it('is not held back by advice', () => {
    // A name that looks taken is still sent: the registry is the authority, and it answers 409.
    expect(toNewSubject(draft({ name: 'acme.orders.OrderCreated' }))).not.toBeNull();
  });

  it('carries the format and the content model as they are', () => {
    const sent = toNewSubject(draft({ format: 'protobuf', contentModel: 'CLOSED' }));

    expect(sent?.format).toBe('protobuf');
    expect(sent?.contentModel).toBe('CLOSED');
  });
});
