import type { CompatibilityMode, CompatibilitySurface } from './compatibility';
import type { FindingSeverity } from './editor-finding';
import { trimRegistryText } from './registry-text';
import { checkSubjectName } from './subject-name';
import type { ContentModel, SchemaFormat } from './wire-tokens';

/** The schema languages a subject can be in, as the form offers them. */
export const FORMAT_CHOICES: readonly { readonly value: SchemaFormat; readonly label: string }[] = [
  { value: 'json', label: 'JSON Schema' },
  { value: 'avro', label: 'Avro' },
  { value: 'protobuf', label: 'Protobuf' },
];

/**
 * Whether documents may carry properties the schema does not describe, as the form offers it.
 *
 * Explicit configuration and never inferred from a document (DESIGN §7), which is also why it is
 * asked here and cannot be changed afterwards: inferring it per version would let it flip
 * silently between v1 and v2 and change the meaning of every verdict after.
 */
export const CONTENT_MODEL_CHOICES: readonly {
  readonly value: ContentModel;
  readonly label: string;
  readonly hint: string;
}[] = [
  {
    value: 'OPEN',
    label: 'Open',
    hint: 'Documents may carry properties the schema does not describe. JSON Schema’s own default.',
  },
  {
    value: 'CLOSED',
    label: 'Closed',
    hint: 'Only described properties are permitted, so adding one affects consumers and is reported.',
  },
];

/** `ActorId.MaxLength`, in UTF-16 code units, counted after the registry has trimmed. */
export const OWNER_MAX_LENGTH = 256;

/** What the form holds, as typed. Nothing here has been judged yet. */
export interface NewSubjectDraft {
  readonly name: string;
  readonly format: SchemaFormat;
  readonly owner: string;
  readonly contentModel: ContentModel;
  /**
   * Both null is "inherit the environment's default", which is not the same as choosing it and
   * is the default (see {@link CompatibilityPolicy}). Exactly one set is a mistake the registry
   * refuses, and the form can reach it by choosing one select and not the other.
   */
  readonly mode: CompatibilityMode | null;
  readonly surface: CompatibilitySurface | null;
}

/** What is sent to create a subject: only what the registry will certainly take. */
export interface NewSubject {
  /** Trimmed, as the registry stores it. */
  readonly name: string;
  readonly format: SchemaFormat;
  /** Trimmed, as the registry stores it. */
  readonly owner: string;
  readonly contentModel: ContentModel;
  /** Null to inherit the environment default. */
  readonly compatibility: {
    readonly mode: CompatibilityMode;
    readonly surface: CompatibilitySurface;
  } | null;
}

/** The fields a finding can be about. */
export type NewSubjectField = 'name' | 'owner' | 'compatibility';

/** One thing to tell the author about the form. */
export interface NewSubjectFinding {
  readonly field: NewSubjectField;
  /**
   * A refusal is something the registry will certainly refuse, and may block the submit; advice
   * never does (ADR-028).
   */
  readonly severity: FindingSeverity;
  /** The registry's own code for a refusal; this client's for advice. */
  readonly code: string;
  readonly message: string;
}

/**
 * Everything worth saying about a draft.
 *
 * <b>The refusals are the registry's own rules and nothing else</b>: the name's grammar and
 * length, the owner's presence and length, and a compatibility policy that is whole or absent.
 * Each was probed against the real create endpoint, not read off the C#, and
 * `e2e/subject-name-parity.spec.ts` holds the name rules against it on every run.
 *
 * <b>What it deliberately does not refuse:</b> the format and the content model, which are
 * choices from a closed list the form cannot get wrong, and a name that already exists. The
 * registry will answer that one with 409 — but *this* list of existing names is a snapshot, and
 * a check that vetoes on a snapshot is wrong in exactly the strict direction ADR-028 rules out:
 * somebody else may have created, or the list may simply be stale. So it is advice, and the
 * registry's own 409 is what refuses.
 *
 * @param existing the subject names the registry last listed in this environment.
 */
export function checkNewSubject(
  draft: NewSubjectDraft,
  existing: readonly string[],
): readonly NewSubjectFinding[] {
  const findings: NewSubjectFinding[] = [];

  const name = checkSubjectName(draft.name);

  if (!name.ok) {
    findings.push({ field: 'name', severity: 'refusal', code: name.code, message: name.message });
  } else {
    findings.push(...adviseAboutExisting(name.name, existing));
  }

  const owner = trimRegistryText(draft.owner);

  if (owner === '') {
    findings.push({
      field: 'owner',
      severity: 'refusal',
      code: 'actor_id_invalid',
      message: 'An owner is required: the person or team answerable for this contract.',
    });
  } else if (owner.length > OWNER_MAX_LENGTH) {
    findings.push({
      field: 'owner',
      severity: 'refusal',
      code: 'actor_id_invalid',
      message: `An owner may be at most ${OWNER_MAX_LENGTH} characters; this one is ${owner.length}.`,
    });
  }

  if ((draft.mode === null) !== (draft.surface === null)) {
    findings.push({
      field: 'compatibility',
      severity: 'refusal',
      code: 'invalid_request',
      message:
        draft.mode === null
          ? 'Choose a mode as well as a surface, or inherit the environment default.'
          : 'Choose a surface as well as a mode, or inherit the environment default.',
    });
  }

  return findings;
}

/**
 * Whether a name already in the list should give someone pause.
 *
 * Two cases, because names are case-sensitive: `orders.Created` and `orders.created` are two
 * subjects, and a registry with both is a registry where a producer and a consumer disagree about
 * one letter and neither is told. The registry creates the second without a word, so this is the
 * only place anybody is asked.
 */
function adviseAboutExisting(
  name: string,
  existing: readonly string[],
): readonly NewSubjectFinding[] {
  if (existing.includes(name)) {
    return [
      {
        field: 'name',
        severity: 'advice',
        code: 'subject_exists',
        message:
          `${name} is already in this environment, so the registry will answer 409 unless this ` +
          'list is out of date. A subject is never removed or renamed, so there is usually ' +
          'nothing to create here: open the existing one instead.',
      },
    ];
  }

  const lower = name.toLowerCase();
  const lookalike = existing.find((other) => other.toLowerCase() === lower);

  return lookalike === undefined
    ? []
    : [
        {
          field: 'name',
          severity: 'advice',
          code: 'subject_differs_in_case',
          message:
            `${lookalike} is already in this environment and differs from this name only in ` +
            'case. Names are case-sensitive, so this would be a second, separate subject.',
        },
      ];
}

/**
 * The request a draft makes, or null while the registry would certainly refuse it.
 *
 * What the confirmation shows and what is sent are both this, so the second click cannot send
 * something the first did not name.
 */
export function toNewSubject(draft: NewSubjectDraft): NewSubject | null {
  const refused = checkNewSubject(draft, []).some((f) => f.severity === 'refusal');
  const name = checkSubjectName(draft.name);

  if (refused || !name.ok) {
    return null;
  }

  return {
    name: name.name,
    format: draft.format,
    owner: trimRegistryText(draft.owner),
    contentModel: draft.contentModel,
    compatibility:
      draft.mode === null || draft.surface === null
        ? null
        : { mode: draft.mode, surface: draft.surface },
  };
}
