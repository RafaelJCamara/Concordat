import { describe, expect, it } from 'vitest';
import { COMPATIBILITY_MODES, COMPATIBILITY_SURFACES, isInherited } from './compatibility';

// The surface spelling in this file is the M6.1 protocol correction (see
// NOTES-FOR-INTEGRATION §3.3), and it is exactly the kind of thing that looks like a typo
// to someone who was not there. `subject-dtos.spec.ts` pins that the old spelling is
// refused at the boundary; these pin the vocabulary the refusal is measured against.

describe('COMPATIBILITY_MODES', () => {
  it('spells the transitive modes with an underscore', () => {
    // Until M6.1 the API derived these from the C# member name and emitted
    // `BACKWARDTRANSITIVE`. The underscored spelling won because it was already what the
    // request side documented and what `BreakingChangeResponse` emitted.
    expect(COMPATIBILITY_MODES).toContain('BACKWARD_TRANSITIVE');
    expect(COMPATIBILITY_MODES).not.toContain('BACKWARDTRANSITIVE');
  });

  it('names all seven modes', () => {
    expect(COMPATIBILITY_MODES).toEqual([
      'NONE',
      'BACKWARD',
      'BACKWARD_TRANSITIVE',
      'FORWARD',
      'FORWARD_TRANSITIVE',
      'FULL',
      'FULL_TRANSITIVE',
    ]);
  });
});

describe('COMPATIBILITY_SURFACES', () => {
  it('has one spelling for the JSON wire surface', () => {
    // The divergence a second client found: a policy said `WIREJSON` and a breaking change
    // said `WIRE_JSON` for the same value, and a single implementation never noticed
    // because it agreed with itself either way.
    expect(COMPATIBILITY_SURFACES).toEqual(['WIRE', 'WIRE_JSON', 'SOURCE']);
  });
});

describe('isInherited', () => {
  it('is true only when the subject sets neither half', () => {
    expect(isInherited({ mode: null, surface: null })).toBe(true);
  });

  it('is false for a policy set on the subject', () => {
    expect(isInherited({ mode: 'BACKWARD', surface: 'WIRE_JSON' })).toBe(false);
  });

  it('is false for a half-specified policy', () => {
    // Neither inheriting nor a complete policy. Treating it as inherited would show a
    // partially pinned subject as if it were following the environment default, and then
    // silently pin the rest of it the first time somebody saved the form. The API refuses
    // this shape, so seeing one means something upstream is wrong — it must not be
    // smoothed over here.
    expect(isInherited({ mode: 'BACKWARD', surface: null })).toBe(false);
    expect(isInherited({ mode: null, surface: 'WIRE_JSON' })).toBe(false);
  });
});
