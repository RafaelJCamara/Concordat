import { formatSubjectRefs, parseSubjectRefs, subjectsRefusal, type SubjectRef } from './contract';

// Changing a binding: what the form can say before the registry is asked.

/** What is sent when a binding is changed. */
export interface BindingChange {
  readonly subjects: readonly SubjectRef[];
  /** Null clears it (and is always null for a consume binding, which has none). */
  readonly precedence: number | null;
}

/** What a person has typed. */
export interface BindingDraft {
  readonly subjects: string;
  readonly precedence: number | null;
}

/** What a binding carries now. */
export interface BindingCurrent {
  readonly subjects: readonly SubjectRef[];
  readonly precedence: number | null;
}

/** Something the form says about the draft. */
export interface BindingFinding {
  /** A refusal blocks; advice does not, but there is nothing to send either way. */
  readonly severity: 'refusal' | 'advice';
  readonly message: string;
}

/** The verdict on a draft: what would be sent, or null, and the reason. */
export interface BindingEditCheck {
  readonly send: BindingChange | null;
  readonly finding: BindingFinding | null;
}

/**
 * Whether a draft can be sent, and what the form should say about it.
 *
 * <b>It blocks only on what the registry certainly refuses, and on a request with nothing in
 * it.</b> Whether the new subjects or precedence would leave this binding overlapping another one
 * is the registry's to say (overlap is the intersection of two patterns, which the browser cannot
 * see), with its own words. What is checked here is the shape: that there is a subject to carry,
 * that a precedence is a whole number, and that the draft is not what the binding already carries,
 * which the registry would answer `200` to and record nothing for. The same split the subject's
 * policy editor makes (ADR-032).
 */
export function checkBindingEdit(
  current: BindingCurrent,
  draft: BindingDraft,
  hasPrecedence: boolean,
): BindingEditCheck {
  const subjects = parseSubjectRefs(draft.subjects);

  if (subjects === null) {
    return {
      send: null,
      finding: { severity: 'refusal', message: subjectsRefusal(draft.subjects) },
    };
  }

  const precedence = hasPrecedence ? draft.precedence : null;

  if (precedence !== null && !Number.isInteger(precedence)) {
    return {
      send: null,
      finding: { severity: 'refusal', message: 'A precedence is a whole number.' },
    };
  }

  // Order counts to the registry (it is stored and shown), so the same references in another
  // order are a change, and only an identical request is not.
  if (
    formatSubjectRefs(subjects) === formatSubjectRefs(current.subjects) &&
    (!hasPrecedence || precedence === current.precedence)
  ) {
    return {
      send: null,
      finding: { severity: 'advice', message: 'That is what this binding carries now.' },
    };
  }

  return { send: { subjects, precedence }, finding: null };
}
