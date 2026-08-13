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

/** Where a version sits in the approval gate (ADR-017). */
export const VERSION_STATUSES = ['ACTIVE', 'AWAITING_APPROVAL', 'REJECTED'] as const;

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
