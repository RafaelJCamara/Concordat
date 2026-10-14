import { describe, expect, it } from 'vitest';
import {
  COMPATIBILITY_DIRECTIONS,
  COMPATIBILITY_FINDING_SURFACE_NOTE,
  COMPATIBILITY_DIRECTION_MEANING,
  COMPATIBILITY_MODE_MEANING,
  COMPATIBILITY_MODES,
  COMPATIBILITY_SURFACE_MEANING,
  COMPATIBILITY_SURFACES,
  isInherited,
} from './compatibility';

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

// The wording is one constant per axis, read by five screens (the subject's Compatibility cell, the
// policy editor, the create-subject form, the environment settings and the finding lists). These
// pin the two ways it can go wrong without anyone noticing: a token with no words, and words for
// a token that no longer exists. They also pin the claims that were checked against the engine,
// so a rewrite that makes a sentence read better and say something else fails here.

describe('the meaning of each token', () => {
  const axes = [
    ['mode', COMPATIBILITY_MODES, COMPATIBILITY_MODE_MEANING],
    ['surface', COMPATIBILITY_SURFACES, COMPATIBILITY_SURFACE_MEANING],
    ['direction', COMPATIBILITY_DIRECTIONS, COMPATIBILITY_DIRECTION_MEANING],
  ] as const;

  for (const [axis, tokens, meaning] of axes) {
    it(`has words for every ${axis}, and for nothing that is not one`, () => {
      expect(Object.keys(meaning).sort()).toEqual([...tokens].sort());
    });

    it(`keeps the ${axis} sentences short, whole and different from each other`, () => {
      const sentences = Object.values(meaning);

      for (const sentence of sentences) {
        // Short because it is read in a form, under a select, by somebody mid-decision.
        expect(sentence.length).toBeGreaterThan(20);
        expect(sentence.length).toBeLessThanOrEqual(190);
        expect(sentence.endsWith('.')).toBe(true);
      }

      expect(new Set(sentences).size).toBe(sentences.length);
    });
  }

  it('says which version each mode is compared against, as SelectPriors does', () => {
    // JsonSchemaCompatibilityChecker.SelectPriors: transitive modes compare against every prior
    // version, the others against the highest ordinal only.
    for (const mode of COMPATIBILITY_MODES) {
      const sentence = COMPATIBILITY_MODE_MEANING[mode];

      if (mode.endsWith('_TRANSITIVE')) {
        expect(sentence, mode).toContain('every earlier version');
      } else if (mode !== 'NONE') {
        expect(sentence, mode).toContain('most recent earlier version');
        expect(sentence, mode).not.toContain('every earlier version');
      }
    }
  });

  it('says what the direction is, in the words of the engine', () => {
    // Compatibility.cs: Backward = the new schema reads data written by the previous one;
    // Forward = the previous schema reads data written by the new one. Swapping them is the
    // mistake this whole axis exists to avoid.
    expect(COMPATIBILITY_MODE_MEANING.BACKWARD).toContain('new version must read data written');
    expect(COMPATIBILITY_MODE_MEANING.FORWARD).toContain(
      'earlier version must read data written under the new',
    );
    expect(COMPATIBILITY_MODE_MEANING.FULL).toContain('Both BACKWARD and FORWARD');
    expect(COMPATIBILITY_MODE_MEANING.NONE).toContain('No compatibility checking');
  });

  it('says that the transitive variants are the same direction as their base', () => {
    expect(COMPATIBILITY_MODE_MEANING.BACKWARD_TRANSITIVE).toContain('As BACKWARD');
    expect(COMPATIBILITY_MODE_MEANING.FORWARD_TRANSITIVE).toContain('As FORWARD');
    expect(COMPATIBILITY_MODE_MEANING.FULL_TRANSITIVE).toContain('As FULL');
  });

  it('says that the surfaces nest, and what is only in the last one', () => {
    // ADR-016: Wire ⊂ WireJson ⊂ Source. A policy is violated by a finding at its surface or
    // a narrower one, so each wider surface blocks everything the one before it does.
    expect(COMPATIBILITY_SURFACE_MEANING.WIRE_JSON).toContain('what WIRE blocks');
    expect(COMPATIBILITY_SURFACE_MEANING.SOURCE).toContain('what WIRE_JSON blocks');
    // IntegerWidenedToNumber_PassesWireJsonAndFailsSource and ChangingFormat_IsSourceOnly:
    // a Source-only finding leaves validation alone and changes generated code.
    expect(COMPATIBILITY_SURFACE_MEANING.SOURCE).toContain('generated code');
    expect(COMPATIBILITY_SURFACE_MEANING.SOURCE).toContain('integer');
  });

  it('is honest that a WIRE policy blocks nothing for JSON Schema', () => {
    // AWirePolicy_IsEffectivelyNoCheckingForJsonSchema: JSON is self-describing, so no JSON
    // Schema difference stops bytes decoding. Leaving this out would let somebody choose WIRE
    // believing it is a looser version of a check that does something.
    expect(COMPATIBILITY_SURFACE_MEANING.WIRE).toContain('JSON Schema');
    expect(COMPATIBILITY_SURFACE_MEANING.WIRE).toContain('blocks nothing');
  });

  it('names the default surface as the default, because ADR-016 chose it', () => {
    expect(COMPATIBILITY_SURFACE_MEANING.WIRE_JSON).toContain('default for a new environment');
  });

  it('describes a finding’s direction as who can no longer read what', () => {
    expect(COMPATIBILITY_DIRECTION_MEANING.BACKWARD).toContain('new version');
    expect(COMPATIBILITY_DIRECTION_MEANING.FORWARD).toContain('earlier version');
  });

  it('says what a finding’s surface is: the narrowest one it breaks, blocked at that or wider', () => {
    // CompatibilityPolicy.IsViolatedBy: finding <= policy surface.
    expect(COMPATIBILITY_FINDING_SURFACE_NOTE).toContain('narrowest surface');
    expect(COMPATIBILITY_FINDING_SURFACE_NOTE).toContain('or a wider one');
  });
});
