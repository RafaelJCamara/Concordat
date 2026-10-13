import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import type { Environment } from '../../../domain/environments/environment';
import { toBroker, toEnvironment, type BrokerDto, type EnvironmentDto } from './environment-dtos';
import { EnvironmentsApi } from './environments-api';

// The URLs, verbs and bodies are what the API file pins; the DTO mapping is pinned beside it
// because a wrong token there is the failure that makes a settings page lie.

const broker: BrokerDto = {
  brokerId: '0a0a0a0a-0a0a-0a0a-0a0a-0a0a0a0a0a0a',
  displayName: 'primary',
  uri: 'amqps://rabbit.example.com:5671',
  virtualHost: '/',
  useTls: true,
  hasCredentials: true,
  status: 'REACHABLE',
  lastCheckedAt: '2026-08-13T09:00:00+00:00',
  lastError: null,
};

const dto: EnvironmentDto = {
  name: 'dev',
  description: 'Shared development',
  defaultCompatibilityPolicy: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
  registrationPolicy: 'OPEN',
  allowPreReleaseVersions: true,
  createdAt: '2026-08-01T09:00:00+00:00',
  brokers: [broker],
};

function setUp() {
  TestBed.configureTestingModule({
    providers: [
      provideConcordatConfig({ defaultEnvironment: 'dev' }),
      provideHttpClient(),
      provideHttpClientTesting(),
    ],
  });

  return { api: TestBed.inject(EnvironmentsApi), backend: TestBed.inject(HttpTestingController) };
}

describe('EnvironmentsApi', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('gets an environment by name, percent-encoded', () => {
    const { api, backend } = setUp();
    api.getEnvironment('dev/eu').subscribe();

    backend.expectOne('/v1/environments/dev%2Feu');
  });

  it('patches only what it is given', () => {
    // The registry leaves an omitted member alone. Sending the whole form would restate the
    // compatibility default and quietly turn an inherited setting into one somebody chose.
    const { api, backend } = setUp();
    api.updateEnvironment('dev', { description: 'new' }).subscribe();

    const request = backend.expectOne('/v1/environments/dev');

    expect(request.request.method).toBe('PATCH');
    expect(request.request.body).toEqual({ description: 'new' });
  });

  it('returns the whole environment as a domain type', () => {
    let environment: Environment | null = null;
    const { api, backend } = setUp();
    api.updateEnvironment('dev', {}).subscribe((value) => (environment = value));

    backend.expectOne('/v1/environments/dev').flush(dto);

    expect(environment!.createdAt).toBeInstanceOf(Date);
    expect(environment!.brokers[0]?.lastCheckedAt).toBeInstanceOf(Date);
  });

  describe('brokers', () => {
    const id = broker.brokerId;

    it('adds one, leaving a blank virtual host off the body', () => {
      // The registry's default is `/`; an empty string is a virtual host named nothing.
      const { api, backend } = setUp();
      api.addBroker('dev', { displayName: 'p', uri: 'amqp://x', virtualHost: '  ' }).subscribe();

      const request = backend.expectOne('/v1/environments/dev/brokers');

      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({ displayName: 'p', uri: 'amqp://x' });
    });

    it('sends a virtual host when given, trimmed', () => {
      const { api, backend } = setUp();
      api
        .addBroker('dev', { displayName: 'p', uri: 'amqp://x', virtualHost: ' /prod ' })
        .subscribe();

      expect(backend.expectOne('/v1/environments/dev/brokers').request.body.virtualHost).toBe(
        '/prod',
      );
    });

    it('checks one with a POST to its check route', () => {
      const { api, backend } = setUp();
      api.checkBroker('dev', id).subscribe();

      expect(backend.expectOne(`/v1/environments/dev/brokers/${id}/check`).request.method).toBe(
        'POST',
      );
    });

    it('puts credentials to the credentials route, and nothing comes back to read', () => {
      let environment: Environment | null = null;
      const { api, backend } = setUp();
      api
        .setBrokerCredential('dev', id, { username: 'u', password: 'p' })
        .subscribe((value) => (environment = value));

      const request = backend.expectOne(`/v1/environments/dev/brokers/${id}/credentials`);

      expect(request.request.method).toBe('PUT');
      expect(request.request.body).toEqual({ username: 'u', password: 'p' });

      request.flush(dto);

      // Write-only (ADR-012): the answer carries whether one exists, never the credential.
      expect(JSON.stringify(environment)).not.toContain('"password"');
      expect(environment!.brokers[0]?.hasCredentials).toBe(true);
    });

    it('deletes credentials and brokers on their own routes', () => {
      const { api, backend } = setUp();
      api.removeBrokerCredential('dev', id).subscribe();
      api.removeBroker('dev', id).subscribe();

      expect(
        backend.expectOne(`/v1/environments/dev/brokers/${id}/credentials`).request.method,
      ).toBe('DELETE');
      expect(backend.expectOne(`/v1/environments/dev/brokers/${id}`).request.method).toBe('DELETE');
    });
  });
});

describe('toEnvironment', () => {
  it('maps the default compatibility as the pair the registry holds', () => {
    expect(toEnvironment(dto).defaultCompatibility).toEqual({
      mode: 'BACKWARD',
      surface: 'WIRE_JSON',
    });
  });

  it.each([
    ['both members', { mode: null, surface: null }],
    ['the mode', { mode: null, surface: 'WIRE' }],
    ['the surface', { mode: 'FULL', surface: null }],
  ])(
    'refuses a null for %s, because an environment never has an unset default (ADR-033)',
    (_which, policy) => {
      expect(() => toEnvironment({ ...dto, defaultCompatibilityPolicy: policy })).toThrow(
        /defaultCompatibilityPolicy/,
      );
    },
  );

  it.each(['OPEN', 'CI_ONLY', 'CLOSED'])('accepts the registration policy %s', (policy) => {
    expect(toEnvironment({ ...dto, registrationPolicy: policy }).registrationPolicy).toBe(policy);
  });

  it('refuses a registration policy it does not recognise, rather than showing it as open', () => {
    expect(() => toEnvironment({ ...dto, registrationPolicy: 'CIONLY' })).toThrow(/CIONLY/);
  });

  it('refuses a compatibility mode it does not recognise', () => {
    expect(() =>
      toEnvironment({
        ...dto,
        defaultCompatibilityPolicy: { mode: 'SIDEWAYS', surface: 'WIRE' },
      }),
    ).toThrow(/SIDEWAYS/);
  });
});

describe('toBroker', () => {
  it.each(['UNKNOWN', 'REACHABLE', 'UNREACHABLE'])('accepts the status %s', (status) => {
    expect(toBroker({ ...broker, status }).status).toBe(status);
  });

  it('refuses a status it does not recognise', () => {
    expect(() => toBroker({ ...broker, status: 'FLAKY' })).toThrow(/FLAKY/);
  });

  it('keeps a never-checked broker as null, not as the epoch', () => {
    expect(toBroker({ ...broker, lastCheckedAt: null }).lastCheckedAt).toBeNull();
  });

  it('carries the broker’s own words about why it failed', () => {
    expect(toBroker({ ...broker, lastError: 'connection refused' }).lastError).toBe(
      'connection refused',
    );
  });
});
