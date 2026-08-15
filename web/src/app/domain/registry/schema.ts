import type { SchemaFormat } from './wire-tokens';

/**
 * One schema's reference to another.
 *
 * `name` is the spelling used *inside* the referring document — a `$ref` target in JSON
 * Schema, a fully-qualified type name in Avro — and it is deliberately not derivable from
 * the subject and version: the same registered schema can be referenced under different
 * local names by two different documents.
 */
export interface SchemaReference {
  /** The name the referring document uses. */
  readonly name: string;
  /** The subject the reference resolves to. */
  readonly subject: string;
  /** The ordinal within that subject. */
  readonly version: number;
}

/**
 * A schema document, addressed by the hash of its canonical form (ADR-015).
 *
 * The id is global and the document is deduplicated by content, so two subjects that
 * register byte-identical schemas share one of these. That is why this type carries no
 * subject and no environment: it is not "this subject's schema at v3", it is the document
 * that v3 happens to point at, and something else may point at it too.
 */
export interface SchemaDocument {
  /** The content-addressed id. */
  readonly schemaId: string;
  readonly format: SchemaFormat;
  /**
   * The document as it was registered, verbatim.
   *
   * Not the canonical form. Canonicalisation is what the id is computed over, and showing
   * it instead would mean a reader who copies what is on screen back into a file gets a
   * document that no longer matches what their producer actually publishes.
   */
  readonly text: string;
  readonly references: readonly SchemaReference[];
}
