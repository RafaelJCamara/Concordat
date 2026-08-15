// The schema wire shapes, and the mapping onto the domain.
//
// Mirrors `SchemaResponse` and `ReferenceResponse` in `Concordat.Api/Contracts.cs`. Kept
// beside `subject-dtos.ts` rather than folded into it: schemas hang off `/v1/schemas`
// rather than the environment root, and they are a separate resource with a separate
// lifetime — a schema outlives every subject that ever pointed at it (ADR-015).

import type { SchemaDocument, SchemaReference } from '../../../domain/registry/schema';
import { SCHEMA_FORMATS, type SchemaFormat } from '../../../domain/registry/wire-tokens';
import { wireToken } from './wire-token';

/** `ReferenceResponse`. */
export interface ReferenceDto {
  readonly name: string;
  readonly subject: string;
  readonly version: number;
}

/** `SchemaResponse`. */
export interface SchemaDto {
  readonly schemaId: string;
  readonly format: string;
  /** The document text, verbatim — not the canonical form. */
  readonly schema: string;
  readonly references: readonly ReferenceDto[];
}

export function toSchemaDocument(dto: SchemaDto): SchemaDocument {
  return {
    schemaId: dto.schemaId,
    format: wireToken('format', dto.format, SCHEMA_FORMATS) as SchemaFormat,
    text: dto.schema,
    references: dto.references.map(toReference),
  };
}

export function toReference(dto: ReferenceDto): SchemaReference {
  return { name: dto.name, subject: dto.subject, version: dto.version };
}
