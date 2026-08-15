import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map, type Observable } from 'rxjs';
import { CONCORDAT_CONFIG } from '../../../core/config/app-config';
import { apiRoot } from '../../../core/http/api-url';
import type { SchemaDocument } from '../../../domain/registry/schema';
import { toSchemaDocument, type SchemaDto } from './schema-dtos';

/**
 * The registry's schema endpoints.
 *
 * **Not environment-scoped**, unlike everything in `SubjectsApi`. Schemas are stored
 * globally and deduplicated by content (ADR-015), so `/v1/schemas/{id}` has no environment
 * segment and adding one would 404. Authorisation is by reachability on the server: a
 * caller may read a schema only if some subject in their tenant references it, and an
 * unreachable id answers 404 rather than 403 — a distinct 403 would confirm that another
 * tenant's schema exists.
 */
@Injectable({ providedIn: 'root' })
export class SchemasApi {
  private readonly http = inject(HttpClient);
  private readonly config = inject(CONCORDAT_CONFIG);

  /**
   * One schema document, by its content-addressed id.
   *
   * The id is hex and fixed-length, so it needs no encoding — but it is encoded anyway,
   * because the day this is called with an id that came from somewhere other than the
   * registry is the day the absence matters.
   */
  getSchema(schemaId: string): Observable<SchemaDocument> {
    return this.http
      .get<SchemaDto>(`${apiRoot(this.config)}/schemas/${encodeURIComponent(schemaId)}`)
      .pipe(map(toSchemaDocument));
  }
}
