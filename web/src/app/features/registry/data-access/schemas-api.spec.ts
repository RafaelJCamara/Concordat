import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { type ConcordatConfig, provideConcordatConfig } from '../../../core/config/app-config';
import type { SchemaDocument } from '../../../domain/registry/schema';
import { SchemasApi } from './schemas-api';
import type { SchemaDto } from './schema-dtos';

// The one thing here that is easy to get wrong is the URL: schemas are *not* environment
// scoped, and building this route the way every other registry route is built produces a 404
// that reads exactly like a missing schema.

const dto: SchemaDto = {
  schemaId: '0123456789abcdef0123456789abcdef',
  format: 'json',
  schema: '{"type":"object"}',
  references: [{ name: 'Money', subject: 'common.money', version: 2 }],
};

function setUp(config: Partial<ConcordatConfig> = {}) {
  TestBed.configureTestingModule({
    providers: [provideConcordatConfig(config), provideHttpClient(), provideHttpClientTesting()],
  });

  return {
    api: TestBed.inject(SchemasApi),
    backend: TestBed.inject(HttpTestingController),
  };
}

describe('SchemasApi', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('addresses the global schema route, with no environment segment', () => {
    // ADR-015: schemas are content-addressed and deduplicated across the whole registry, so
    // there is no environment to scope them to and adding one would 404.
    const { api, backend } = setUp();
    api.getSchema('0123456789abcdef0123456789abcdef').subscribe();

    backend.expectOne('/v1/schemas/0123456789abcdef0123456789abcdef');
  });

  it('returns a domain type rather than the DTO', () => {
    let document: SchemaDocument | null = null;
    const { api, backend } = setUp();
    api.getSchema(dto.schemaId).subscribe((value) => (document = value));

    backend.expectOne(`/v1/schemas/${dto.schemaId}`).flush(dto);

    // `text`, not `schema`: the wire spelling stops at this boundary.
    expect(document!.text).toBe('{"type":"object"}');
    expect(document!.references).toEqual([{ name: 'Money', subject: 'common.money', version: 2 }]);
  });

  it('encodes the id', () => {
    const { api, backend } = setUp();
    api.getSchema('not/an/id').subscribe();

    backend.expectOne('/v1/schemas/not%2Fan%2Fid');
  });

  it('addresses the configured registry rather than its own origin', () => {
    const { api, backend } = setUp({
      apiBaseUrl: 'https://registry.example.com',
      profile: 'cloud',
    });
    api.getSchema('abc').subscribe();

    backend.expectOne('https://registry.example.com/v1/schemas/abc');
  });
});
