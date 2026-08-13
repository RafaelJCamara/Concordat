import type { CompatibilityPolicy } from './compatibility';
import type { ContentModel, SchemaFormat, SubjectLifecycle, VersionStatus } from './wire-tokens';

/** One registered version of a subject's contract. */
export interface SchemaVersion {
  /** The canonical identity within the subject. Contiguous and monotonic. */
  readonly ordinal: number;
  /** The content-addressed schema id. Note: the id, never the schema text. */
  readonly schemaId: string;
  /** The optional verified intent label. */
  readonly semanticVersion: string | null;
  readonly status: VersionStatus;
  readonly changelog: string | null;
  readonly registeredAt: Date;
  readonly registeredBy: string;
  readonly deprecated: boolean;
}

/** A subject and its versions. */
export interface Subject {
  readonly name: string;
  readonly format: SchemaFormat;
  readonly owner: string;
  readonly lifecycle: SubjectLifecycle;
  readonly contentModel: ContentModel;
  readonly compatibilityPolicy: CompatibilityPolicy;
  /**
   * The gated `latest` pointer (ADR-017), or null when no version is active.
   *
   * This is an ordinal the server chose, not "the highest ordinal". A version awaiting
   * approval has a higher ordinal and is deliberately not pointed at; deriving `latest`
   * client-side would reintroduce exactly the defect the gate exists to prevent.
   */
  readonly latest: number | null;
  readonly versions: readonly SchemaVersion[];
}

/** The version the `latest` pointer resolves to, or null when the subject has none. */
export function latestVersion(subject: Subject): SchemaVersion | null {
  if (subject.latest === null) {
    return null;
  }

  return subject.versions.find((v) => v.ordinal === subject.latest) ?? null;
}

/** Versions registered but held at the approval gate, newest first. */
export function pendingVersions(subject: Subject): readonly SchemaVersion[] {
  return subject.versions
    .filter((v) => v.status === 'AWAITING_APPROVAL')
    .sort((a, b) => b.ordinal - a.ordinal);
}
