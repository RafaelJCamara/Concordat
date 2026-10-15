import {
  isInherited,
  type CompatibilityMode,
  type CompatibilityPolicy,
  type CompatibilitySurface,
} from './compatibility';
import type { FindingSeverity } from './editor-finding';
import { OWNER_MAX_LENGTH, ownerLengthRefusal } from './new-subject';
import { trimRegistryText } from './registry-text';

// What can be changed about a subject after it exists: its owner, its compatibility policy and who may
// decide its held versions, and nothing else (ADR-030 probed the rest; ADR-032 records the first two and
// ADR-043 the third).

/** The fields a finding can be about. */
export type SubjectSettingsField = 'owner' | 'policy' | 'reviewers';

/** One thing to tell the author about an edit. */
export interface SubjectSettingsFinding {
  readonly field: SubjectSettingsField;
  /**
   * A refusal is something the registry will certainly refuse. Advice is this client's remark and
   * never the registry's (ADR-028). Neither is what stops a no-op: see {@link OwnerEdit.send}.
   */
  readonly severity: FindingSeverity;
  /** The registry's own code for a refusal; this client's for advice. */
  readonly code: string;
  readonly message: string;
}

/**
 * What an owner edit comes to.
 *
 * <b>`send` is the request, or null when there is none to make.</b> That is the one thing the
 * submit button reads, and it is deliberately not "no refusals": two drafts are not refused by
 * the registry and still have nothing to send.
 *
 * - **Blank.** `PATCH` with an empty or whitespace-only owner answers 200 and changes nothing
 *   (it used to answer 500 for whitespace; ADR-032). There is no request that means "blank", so
 *   an empty field is a field nobody has filled in.
 * - **The owner already shown.** The registry would accept it and record an audit entry
 *   ("owner a -> a") for a change that did not happen.
 *
 * The second is the one place this form is knowingly stricter than the registry, and it is
 * stricter only in the case where the snapshot is stale: somebody else changed the owner since
 * this page read it, and the person wants it back to what the page still shows. The editor opens
 * on a page that has just read the registry and re-reads after every save, and the message says
 * "as of the last read", so the way out is a reload. ADR-032 records the trade.
 */
export interface OwnerEdit {
  /** The owner to send, trimmed as the registry will store it, or null when there is nothing to send. */
  readonly send: string | null;
  readonly findings: readonly SubjectSettingsFinding[];
}

/**
 * Judges an owner as typed against the one the page last read.
 *
 * The refusals are the registry's own: after the trim `System.String.Trim()` does (not
 * JavaScript's `trim()`, which disagrees in both directions, see `registry-text.ts`), an owner
 * may be at most 256 characters. Nothing else is asked of one: an email, a team and a line break
 * are all accepted, and `e2e/subject-settings-parity.spec.ts` holds each against the real
 * `PATCH`.
 *
 * @param current the owner the registry last reported, compared exactly (it is stored as sent,
 *   so `Alice` and `alice` are two owners, and one that begins with a character the registry's
 *   trim does not strip is not equal to the same text without it).
 */
export function checkOwnerEdit(current: string, typed: string): OwnerEdit {
  const owner = trimRegistryText(typed);

  if (owner === '') {
    return {
      send: null,
      findings: [
        {
          field: 'owner',
          severity: 'advice',
          code: 'owner_blank',
          message: 'Enter the new owner. The registry ignores a blank one.',
        },
      ],
    };
  }

  const tooLong = ownerLengthRefusal(owner);

  if (tooLong !== null) {
    return {
      send: null,
      findings: [
        { field: 'owner', severity: 'refusal', code: 'actor_id_invalid', message: tooLong },
      ],
    };
  }

  if (owner === current) {
    return {
      send: null,
      findings: [
        {
          field: 'owner',
          severity: 'advice',
          code: 'owner_unchanged',
          message: `${owner} is already the owner, as of the last time this page read the registry.`,
        },
      ],
    };
  }

  return { send: owner, findings: [] };
}

/** The most reviewers a subject may name (`Subject.MaxReviewers`, ADR-043). */
export const REVIEWERS_MAX = 20;

/**
 * What a reviewer-set edit comes to.
 *
 * <b>`send` is the whole set to send, or null when there is nothing to send.</b> An empty array is a
 * request and a real one: it clears the set, so that any holder of the scope may decide again. Null
 * is for a draft the registry would refuse, and for one that is the set the page already shows (the
 * registry would accept it and write nothing, so a confirmation about it would describe a change
 * that is not one).
 */
export interface ReviewersEdit {
  readonly send: readonly string[] | null;
  readonly findings: readonly SubjectSettingsFinding[];
}

/**
 * Judges a reviewer list as typed, one actor per line, against the set the page last read.
 *
 * <b>One per line and not comma-separated</b>, because an actor id is any text up to 256 characters
 * and an API key's label can hold a comma; a separator would turn one reviewer into two. Each line is
 * trimmed as the registry trims (`registry-text.ts`), a blank line is no reviewer, and a repeat in
 * any case is dropped with the first spelling kept, which is what the registry does and what the
 * confirmation therefore shows.
 *
 * The refusals are the registry's own: an actor id over 256 characters, and more than
 * {@link REVIEWERS_MAX} distinct reviewers (counted after the repeats are dropped, as it counts).
 * The registry does not check that a reviewer exists, so neither does this: a reviewer can be a
 * person's email address or an API key's label, and nothing here could tell the two apart.
 *
 * @param current the set the registry last reported, compared exactly and in order (case and order
 *   are part of what was set).
 * @param actor who is signed in, when known. A non-empty set that does not name them is not refused (a
 *   team may well hand a subject to someone else), but it is the one way to lock yourself out of
 *   deciding, so it is said.
 */
export function checkReviewersEdit(
  current: readonly string[],
  typed: string,
  actor: string | null = null,
): ReviewersEdit {
  const entries = typed
    .split(/\r\n|\r|\n/)
    .map(trimRegistryText)
    .filter((line) => line !== '');

  const distinct: string[] = [];

  for (const entry of entries) {
    if (!distinct.some((d) => d.toLowerCase() === entry.toLowerCase())) {
      distinct.push(entry);
    }
  }

  const tooLong = distinct.find((d) => d.length > OWNER_MAX_LENGTH);

  if (tooLong !== undefined) {
    return {
      send: null,
      findings: [
        {
          field: 'reviewers',
          severity: 'refusal',
          code: 'actor_id_invalid',
          message: `A reviewer may be at most ${OWNER_MAX_LENGTH} characters; one is ${tooLong.length}.`,
        },
      ],
    };
  }

  if (distinct.length > REVIEWERS_MAX) {
    return {
      send: null,
      findings: [
        {
          field: 'reviewers',
          severity: 'refusal',
          code: 'reviewers_invalid',
          message: `A subject may name at most ${REVIEWERS_MAX} reviewers; this names ${distinct.length}.`,
        },
      ],
    };
  }

  if (distinct.length === current.length && distinct.every((d, i) => d === current[i])) {
    return {
      send: null,
      findings: [
        {
          field: 'reviewers',
          severity: 'advice',
          code: 'reviewers_unchanged',
          message:
            current.length === 0
              ? 'No reviewers are named, as of the last time this page read the registry.'
              : 'These are already the reviewers, as of the last time this page read the registry.',
        },
      ],
    };
  }

  const lockedOut =
    actor !== null &&
    distinct.length > 0 &&
    !distinct.some((d) => d.toLowerCase() === actor.toLowerCase());

  return {
    send: distinct,
    findings: lockedOut
      ? [
          {
            field: 'reviewers',
            severity: 'advice',
            code: 'reviewers_exclude_you',
            message: `You (${actor}) are not in this list, so you could not approve or reject a version of this subject afterwards unless you are added again.`,
          },
        ]
      : [],
  };
}

/** What the policy form holds, as chosen. Nothing here has been judged yet. */
export interface PolicyDraft {
  /** Null is "inherit". Exactly one of the pair null is a mistake the registry refuses. */
  readonly mode: CompatibilityMode | null;
  readonly surface: CompatibilitySurface | null;
}

/**
 * What a policy edit comes to.
 *
 * `send` is the whole policy, or both members null to clear it and inherit. It is null when the
 * registry would refuse the pair, and when the draft is the policy the page already shows (nothing
 * to change, and a confirmation that says "takes effect for the next registration" about a
 * change that is not one is a sentence that cannot be true).
 */
export interface PolicyEdit {
  readonly send: CompatibilityPolicy | null;
  readonly findings: readonly SubjectSettingsFinding[];
}

/**
 * Judges a policy as chosen against the one the page last read.
 *
 * <b>The only refusal is the registry's: both or neither.</b> A mode with no surface, or the
 * reverse, is `invalid_request`, probed. The mode and the surface are each chosen from the
 * existing token lists by a select, so the registry's other refusals (an unknown token, an
 * undefined number, a comma list) cannot be sent from here and are held against the registry by
 * `e2e/subject-settings-parity.spec.ts` instead, so that the day a select is replaced by
 * something typed they are already measured.
 *
 * Both null is not an absent policy and not "the default": it is a request to remove this
 * subject's own policy (`isInherited`), which is a different thing from choosing whatever the
 * default currently is. Choosing it pins the subject, and it then stops following the default.
 */
export function checkPolicyEdit(current: CompatibilityPolicy, draft: PolicyDraft): PolicyEdit {
  if ((draft.mode === null) !== (draft.surface === null)) {
    return {
      send: null,
      findings: [
        {
          field: 'policy',
          severity: 'refusal',
          code: 'invalid_request',
          message:
            draft.mode === null
              ? 'Choose a mode as well as a surface, or inherit the environment default.'
              : 'Choose a surface as well as a mode, or inherit the environment default.',
        },
      ],
    };
  }

  if (draft.mode === current.mode && draft.surface === current.surface) {
    return {
      send: null,
      findings: [
        {
          field: 'policy',
          severity: 'advice',
          code: 'policy_unchanged',
          message: isInherited(current)
            ? 'This subject already has no policy of its own, as of the last time this page read the registry.'
            : 'This is already the policy, as of the last time this page read the registry.',
        },
      ],
    };
  }

  return { send: { mode: draft.mode, surface: draft.surface }, findings: [] };
}

/** A policy as a person reads it: "BACKWARD · WIRE_JSON", or "Inherited" when the subject sets none. */
export function describePolicy(policy: CompatibilityPolicy): string {
  return policy.mode === null || policy.surface === null
    ? 'Inherited'
    : `${policy.mode} · ${policy.surface}`;
}
