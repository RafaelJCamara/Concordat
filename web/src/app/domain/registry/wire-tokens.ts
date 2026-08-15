// The registry's normative vocabulary (ADR-019).
//
// These literals are the wire spellings, not a UI-side translation of them. Inventing a
// second vocabulary — 'awaiting-approval' for the API's 'AWAITING_APPROVAL' — would buy a
// prettier template at the cost of a mapping table in every direction, and ADR-019 already
// guarantees the wire tokens will not be renamed underneath us. Where a token really is
// unfit for display, that is a presentation concern and belongs in a pipe.

/** The schema languages the registry understands. */
export const SCHEMA_FORMATS = ['json', 'avro', 'protobuf'] as const;

/** A schema language, spelled as the API spells it. */
export type SchemaFormat = (typeof SCHEMA_FORMATS)[number];

/**
 * Where a version sits in the approval gate (ADR-017).
 *
 * `DISMISSED` is a pending version whose change was reverted before anyone reviewed it — the
 * registry closes it rather than leaving a proposal nobody will ever act on. It was added with
 * M7 and this list did not learn it until 2026-08-15, which broke the subject list outright for
 * any environment containing one: the unknown-token guard is strict, so one dismissed version
 * failed the whole page. `WireTokenTests` now compares this list against the .NET enum.
 */
export const VERSION_STATUSES = ['ACTIVE', 'AWAITING_APPROVAL', 'REJECTED', 'DISMISSED'] as const;

/** A version's status. */
export type VersionStatus = (typeof VERSION_STATUSES)[number];

/** A subject's lifecycle. `RETIRED` is the soft delete and is terminal. */
export const SUBJECT_LIFECYCLES = ['ACTIVE', 'DEPRECATED', 'RETIRED'] as const;

/** A subject's lifecycle state. */
export type SubjectLifecycle = (typeof SUBJECT_LIFECYCLES)[number];

/** Whether undescribed properties are permitted. */
export const CONTENT_MODELS = ['OPEN', 'CLOSED'] as const;

/** A subject's content model. */
export type ContentModel = (typeof CONTENT_MODELS)[number];
