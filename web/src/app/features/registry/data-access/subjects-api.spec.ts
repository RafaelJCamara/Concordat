import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { type ConcordatConfig, provideConcordatConfig } from '../../../core/config/app-config';
import type { VersionDiff } from '../../../domain/registry/diff';
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

    describe('with a prefix (ADR-038)', () => {
      const withQuery = (url: string) => (request: { urlWithParams: string }) =>
        request.urlWithParams === url;

      it('asks the registry to filter, with the prefix as a query parameter', () => {
        context.api.listSubjects('dev', 'acme.orders.').subscribe();

        context.backend.expectOne(withQuery('/v1/environments/dev/subjects?prefix=acme.orders.'));
      });

      it('sends no parameter for no prefix, so the unfiltered request is the one it always was', () => {
        context.api.listSubjects('dev', '').subscribe();
        context.api.listSubjects('dev', undefined).subscribe();

        const calls = context.backend.match('/v1/environments/dev/subjects');
        expect(calls).toHaveLength(2);
      });

      it('keeps the case, because names are case-sensitive', () => {
        context.api.listSubjects('dev', 'Acme.OrderCreated').subscribe();

        context.backend.expectOne(
          withQuery('/v1/environments/dev/subjects?prefix=Acme.OrderCreated'),
        );
      });

      it('sends an underscore as an underscore: it is a name character, not a wildcard', () => {
        context.api.listSubjects('dev', 'acme.a_').subscribe();

        context.backend.expectOne(withQuery('/v1/environments/dev/subjects?prefix=acme.a_'));
      });

      it('percent-encodes what would otherwise end the value or be read as something else', () => {
        // The registry answers an empty list to all of these. What matters here is that they
        // arrive as one parameter's value and cannot add a parameter or end the URL: a `&`
        // unencoded would become a second parameter, and a `#` would drop the rest. (Angular
        // leaves a `=` inside a value alone, which is legal and read as part of the value.)
        context.api.listSubjects('dev', 'a%b&c=d#e f').subscribe();

        context.backend.expectOne(
          withQuery('/v1/environments/dev/subjects?prefix=a%25b%26c=d%23e%20f'),
        );
      });

      it('returns the same domain types as the unfiltered list', () => {
        let subjects: readonly Subject[] = [];
        context.api.listSubjects('dev', 'orders.').subscribe((value) => (subjects = value));

        context.backend
          .expectOne(withQuery('/v1/environments/dev/subjects?prefix=orders.'))
          .flush([dto]);

        expect(subjects[0]?.name).toBe('orders.created');
        expect(subjects[0]?.versions[0]?.registeredAt).toBeInstanceOf(Date);
      });
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

  describe('createSubject', () => {
    const request = {
      name: 'orders.created',
      format: 'json',
      owner: 'orders-team',
      contentModel: 'OPEN',
      compatibility: null,
    } as const;

    it('posts to the environment’s subjects, with only what was chosen', () => {
      const { api, backend } = setUp();
      api.createSubject('dev', request).subscribe();

      const call = backend.expectOne('/v1/environments/dev/subjects');

      expect(call.request.method).toBe('POST');
      // Inheriting is the *absence* of both policy members, not two nulls.
      expect(call.request.body).toEqual({
        name: 'orders.created',
        format: 'json',
        owner: 'orders-team',
        contentModel: 'OPEN',
      });
    });

    it('sends a whole policy as the registry’s two members', () => {
      const { api, backend } = setUp();
      api
        .createSubject('dev', {
          ...request,
          compatibility: { mode: 'FULL_TRANSITIVE', surface: 'SOURCE' },
        })
        .subscribe();

      const call = backend.expectOne('/v1/environments/dev/subjects');

      expect(call.request.body).toMatchObject({
        compatibilityMode: 'FULL_TRANSITIVE',
        compatibilitySurface: 'SOURCE',
      });
    });

    it('hands back the subject as the registry stored it, not what was sent', () => {
      // The registry trims before it validates, so the stored name can differ from the one sent,
      // and a caller that navigated to what it sent would land on a 404.
      let created: Subject | null = null;
      const { api, backend } = setUp();
      api.createSubject('dev', request).subscribe((value) => (created = value));

      backend
        .expectOne('/v1/environments/dev/subjects')
        .flush({ ...dto, name: 'stored.by.the.registry', latest: null, versions: [] });

      expect((created as Subject | null)?.name).toBe('stored.by.the.registry');
      expect((created as Subject | null)?.versions).toEqual([]);
    });

    it('does not send the owner as the writer or anything else the registry already knows', () => {
      const { api, backend } = setUp();
      api.createSubject('dev', request).subscribe();

      const body = backend.expectOne('/v1/environments/dev/subjects').request.body as object;

      expect(Object.keys(body).sort()).toEqual(['contentModel', 'format', 'name', 'owner']);
    });
  });

  describe('changeOwner', () => {
    it('patches the subject with the owner and nothing else', () => {
      // Never `deprecate`: that is advisory, notifies every subscriber and has no inverse, and a
      // body that could carry it by accident is a screen that could deprecate a subject while
      // meaning to rename its owner.
      const { api, backend } = setUp();
      api.changeOwner('dev', 'orders.created', 'new-team').subscribe();

      const call = backend.expectOne('/v1/environments/dev/subjects/orders.created');

      expect(call.request.method).toBe('PATCH');
      expect(call.request.body).toEqual({ owner: 'new-team' });
    });

    it('percent-encodes the subject name', () => {
      const { api, backend } = setUp();
      api.changeOwner('dev', 'a/b', 'x').subscribe();

      backend.expectOne('/v1/environments/dev/subjects/a%2Fb');
    });

    it('hands back the subject the registry answered with, as a domain type', () => {
      // The registry trims, so the owner in the answer is the stored one.
      let changed: Subject | null = null;
      const { api, backend } = setUp();
      api.changeOwner('dev', 'orders.created', '  stored  ').subscribe((v) => (changed = v));

      backend
        .expectOne('/v1/environments/dev/subjects/orders.created')
        .flush({ ...dto, owner: 'stored' });

      expect((changed as Subject | null)?.owner).toBe('stored');
      expect((changed as Subject | null)?.versions[0]?.registeredAt).toBeInstanceOf(Date);
    });
  });

  describe('setPolicy', () => {
    it('puts both members to the policy resource', () => {
      const { api, backend } = setUp();
      api.setPolicy('dev', 'orders.created', { mode: 'FULL', surface: 'SOURCE' }).subscribe();

      const call = backend.expectOne(
        '/v1/environments/dev/subjects/orders.created/compatibility-policy',
      );

      expect(call.request.method).toBe('PUT');
      expect(call.request.body).toEqual({ mode: 'FULL', surface: 'SOURCE' });
    });

    it('clears the policy with two nulls, which is a request and not an empty body', () => {
      const { api, backend } = setUp();
      api.setPolicy('dev', 'orders.created', { mode: null, surface: null }).subscribe();

      const call = backend.expectOne(
        '/v1/environments/dev/subjects/orders.created/compatibility-policy',
      );

      expect(call.request.body).toEqual({ mode: null, surface: null });
    });

    it('percent-encodes the subject name', () => {
      const { api, backend } = setUp();
      api.setPolicy('dev', 'a/b', { mode: null, surface: null }).subscribe();

      backend.expectOne('/v1/environments/dev/subjects/a%2Fb/compatibility-policy');
    });

    it('answers with the policy the registry stored, not the subject and not what was sent', () => {
      // `PUT` returns a `PolicyResponse`. The registry reads `backward` as `BACKWARD`, and what it
      // says is what the screen reports.
      let stored: unknown = null;
      const { api, backend } = setUp();
      api
        .setPolicy('dev', 'orders.created', { mode: 'BACKWARD', surface: 'WIRE' })
        .subscribe((value) => (stored = value));

      backend
        .expectOne('/v1/environments/dev/subjects/orders.created/compatibility-policy')
        .flush({ mode: 'BACKWARD_TRANSITIVE', surface: 'WIRE_JSON' });

      expect(stored).toEqual({ mode: 'BACKWARD_TRANSITIVE', surface: 'WIRE_JSON' });
    });

    it('reads nulls together as inheriting', () => {
      let stored: unknown = null;
      const { api, backend } = setUp();
      api
        .setPolicy('dev', 'orders.created', { mode: null, surface: null })
        .subscribe((value) => (stored = value));

      backend
        .expectOne('/v1/environments/dev/subjects/orders.created/compatibility-policy')
        .flush({ mode: null, surface: null });

      expect(stored).toEqual({ mode: null, surface: null });
    });

    it('refuses a token this build does not know rather than showing it', () => {
      let failed: unknown = null;
      const { api, backend } = setUp();
      api.setPolicy('dev', 'orders.created', { mode: 'FULL', surface: 'WIRE' }).subscribe({
        error: (error: unknown) => (failed = error),
      });

      backend
        .expectOne('/v1/environments/dev/subjects/orders.created/compatibility-policy')
        .flush({ mode: 'SIDEWAYS', surface: 'WIRE' });

      expect(String(failed)).toContain('SIDEWAYS');
    });
  });

  describe('diffVersions', () => {
    const diff = {
      from: 1,
      to: 2,
      fromSchemaId: 'aaa',
      toSchemaId: 'bbb',
      identical: false,
      policy: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
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
    };

    it('addresses the pair the way the API does, baseline first', () => {
      // `from` is the baseline. Swapping the two would report the inverse change — a removal
      // where there was an addition — under a URL that looks exactly the same.
      const { api, backend } = setUp();
      api.diffVersions('dev', 'orders.created', 1, 2).subscribe();

      backend.expectOne('/v1/environments/dev/subjects/orders.created/versions/1/diff/2');
    });

    it('percent-encodes the subject name', () => {
      const { api, backend } = setUp();
      api.diffVersions('dev', 'orders/created', 1, 2).subscribe();

      backend.expectOne('/v1/environments/dev/subjects/orders%2Fcreated/versions/1/diff/2');
    });

    it('returns a domain diff, with the divergences mapped', () => {
      let result: VersionDiff | null = null;
      const { api, backend } = setUp();
      api.diffVersions('dev', 'orders.created', 1, 2).subscribe((value) => (result = value));

      backend
        .expectOne('/v1/environments/dev/subjects/orders.created/versions/1/diff/2')
        .flush(diff);

      expect(result!.identical).toBe(false);
      expect(result!.policy).toEqual({ mode: 'BACKWARD', surface: 'WIRE_JSON' });
      expect(result!.divergences[0]?.path).toBe('/properties/discount');
    });
  });

  describe('decideVersion', () => {
    it.each(['approve', 'reject'] as const)(
      'posts to the %s route for that ordinal',
      (decision) => {
        const { api, backend } = setUp();
        api.decideVersion('dev', 'orders.created', 2, decision, 'alice').subscribe();

        const request = backend.expectOne(
          `/v1/environments/dev/subjects/orders.created/versions/2/${decision}`,
        );

        expect(request.request.method).toBe('POST');
        expect(request.request.body).toEqual({ decidedBy: 'alice' });
      },
    );

    it('percent-encodes the subject name', () => {
      const { api, backend } = setUp();
      api.decideVersion('dev', 'orders/created', 2, 'approve', 'alice').subscribe();

      backend.expectOne('/v1/environments/dev/subjects/orders%2Fcreated/versions/2/approve');
    });

    it('returns the subject as it stands afterwards, as a domain type', () => {
      let subject: Subject | null = null;
      const { api, backend } = setUp();
      api
        .decideVersion('dev', 'orders.created', 2, 'approve', 'alice')
        .subscribe((value) => (subject = value));

      backend
        .expectOne('/v1/environments/dev/subjects/orders.created/versions/2/approve')
        .flush(dto);

      expect(subject!.versions[0]?.registeredAt).toBeInstanceOf(Date);
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
