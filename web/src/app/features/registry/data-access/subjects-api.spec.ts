import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { type ConcordatConfig, provideConcordatConfig } from '../../../core/config/app-config';
import type { RegistrationOutcome } from '../../../domain/registry/registration';
import type { Subject } from '../../../domain/registry/subject';
import { SubjectsApi } from './subjects-api';
import type { SubjectDto } from './subject-dtos';

// The only place in the registry feature that touches `HttpClient`. Two things are worth
// pinning: the URL it builds, because a wrong one 404s in a way that looks like missing
// data; and that callers are handed domain types, because a `SubjectDto` escaping here
// means the mapping is no longer at the boundary.

const dto: SubjectDto = {
  name: 'orders.created',
  format: 'json',
  owner: 'orders-team',
  lifecycle: 'ACTIVE',
  contentModel: 'OPEN',
  compatibilityPolicy: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
  latest: 1,
  versions: [
    {
      ordinal: 1,
      schemaId: '0123456789abcdef0123456789abcdef',
      semanticVersion: '1.0.0',
      status: 'ACTIVE',
      changelog: null,
      registeredAt: '2026-08-13T09:00:00+00:00',
      registeredBy: 'ci',
      deprecated: false,
    },
  ],
};

function setUp(config: Partial<ConcordatConfig> = {}) {
  TestBed.configureTestingModule({
    providers: [provideConcordatConfig(config), provideHttpClient(), provideHttpClientTesting()],
  });

  return {
    api: TestBed.inject(SubjectsApi),
    backend: TestBed.inject(HttpTestingController),
  };
}

describe('SubjectsApi', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  describe('listSubjects', () => {
    let context: ReturnType<typeof setUp>;

    beforeEach(() => (context = setUp()));

    it('scopes the request to the environment, under the version prefix', () => {
      context.api.listSubjects('dev').subscribe();

      context.backend.expectOne('/v1/environments/dev/subjects');
    });

    it('returns domain types, with timestamps already parsed', () => {
      // A caller handed a raw DTO will eventually reach into a wire field, and the string
      // `registeredAt` would then be sorted and compared as text.
      let subjects: readonly Subject[] = [];
      context.api.listSubjects('dev').subscribe((value) => (subjects = value));

      context.backend.expectOne('/v1/environments/dev/subjects').flush([dto]);

      expect(subjects[0]?.versions[0]?.registeredAt).toBeInstanceOf(Date);
      expect(subjects[0]?.versions[0]?.registeredAt.toISOString()).toBe('2026-08-13T09:00:00.000Z');
    });
  });

  describe('getSubject', () => {
    it('percent-encodes the subject name', () => {
      // Subject names are user-chosen and dotted. One containing a slash would otherwise
      // address a different route entirely, and the registry would answer 404 for a subject
      // that exists.
      const { api, backend } = setUp();
      api.getSubject('dev', 'orders/created').subscribe();

      backend.expectOne('/v1/environments/dev/subjects/orders%2Fcreated');
    });

    it('leaves an ordinary dotted name readable', () => {
      const { api, backend } = setUp();
      api.getSubject('dev', 'orders.created').subscribe();

      backend.expectOne('/v1/environments/dev/subjects/orders.created');
    });
  });

  describe('registerVersion', () => {
    const registration = {
      subject: 'orders.created',
      ordinal: 2,
      schemaId: 'abc',
      status: 'AWAITING_APPROVAL',
      created: true,
      divergences: [
        {
          path: '/properties/discount',
          kind: 'required_field_added',
          direction: 'BACKWARD',
          surface: 'WIRE_JSON',
          message: 'A required field was added.',
          conflictsWithVersion: 1,
        },
      ],
      portability: [
        {
          path: '/properties/amount',
          kind: 'big_decimal',
          severity: 'WARNING',
          message: 'Floats.',
        },
      ],
    };

    it('posts to the subject’s versions collection', () => {
      const { api, backend } = setUp();
      api
        .registerVersion('dev', 'orders.created', {
          schema: '{}',
          semanticVersion: null,
          changelog: null,
        })
        .subscribe();

      const request = backend.expectOne('/v1/environments/dev/subjects/orders.created/versions');

      expect(request.request.method).toBe('POST');
    });

    it('does not send registeredBy, because the server knows who is calling', () => {
      // M8.2 attributes the write to the caller's own identity. A client-supplied name would
      // be a second, unverified answer to a question the server has already answered.
      const { api, backend } = setUp();
      api
        .registerVersion('dev', 'orders.created', {
          schema: '{}',
          semanticVersion: '1.1.0',
          changelog: 'note',
        })
        .subscribe();

      const request = backend.expectOne('/v1/environments/dev/subjects/orders.created/versions');

      expect(request.request.body).toEqual({
        schema: '{}',
        semanticVersion: '1.1.0',
        changelog: 'note',
      });
    });

    it('returns a breaking change as a successful outcome, not an error', () => {
      // The registry accepted it and held it at the gate (ADR-017). Nothing on this path may
      // treat divergences as a failure.
      let outcome: RegistrationOutcome | null = null;
      const { api, backend } = setUp();
      api
        .registerVersion('dev', 'orders.created', {
          schema: '{}',
          semanticVersion: null,
          changelog: null,
        })
        .subscribe((value) => (outcome = value));

      backend
        .expectOne('/v1/environments/dev/subjects/orders.created/versions')
        .flush(registration, { status: 201, statusText: 'Created' });

      expect(outcome!.status).toBe('AWAITING_APPROVAL');
      expect(outcome!.created).toBe(true);
      expect(outcome!.divergences).toHaveLength(1);
    });

    it('drops the portability severity, which has one value on this path', () => {
      let outcome: RegistrationOutcome | null = null;
      const { api, backend } = setUp();
      api
        .registerVersion('dev', 'orders.created', {
          schema: '{}',
          semanticVersion: null,
          changelog: null,
        })
        .subscribe((value) => (outcome = value));

      backend
        .expectOne('/v1/environments/dev/subjects/orders.created/versions')
        .flush(registration);

      expect(outcome!.portability[0]).toEqual({
        path: '/properties/amount',
        kind: 'big_decimal',
        message: 'Floats.',
      });
    });

    it('percent-encodes the subject name', () => {
      const { api, backend } = setUp();
      api
        .registerVersion('dev', 'orders/created', {
          schema: '{}',
          semanticVersion: null,
          changelog: null,
        })
        .subscribe();

      backend.expectOne('/v1/environments/dev/subjects/orders%2Fcreated/versions');
    });
  });

  it('addresses the configured registry rather than its own origin', () => {
    // The same bundle is served by every deployment. A Cloud deployment overrides
    // `apiBaseUrl` at bootstrap, and nothing about the API location is baked in.
    const { api, backend } = setUp({
      apiBaseUrl: 'https://registry.example.com',
      profile: 'cloud',
    });
    api.listSubjects('dev').subscribe();

    backend.expectOne('https://registry.example.com/v1/environments/dev/subjects');
  });
});
