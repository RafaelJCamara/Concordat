import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import type { Contract } from '../../../domain/contracts/contract';
import { ContractsApi } from './contracts-api';
import type { ContractDto } from './contract-dtos';

// The URLs, the verbs and the bodies are what this file pins. A wrong one 404s or 405s in a way
// that looks like a missing contract, and a body that sends a blank virtual host creates a
// binding on a vhost named nothing.

const dto: ContractDto = {
  name: 'payments',
  enforcement: 'MONITOR',
  createdAt: '2026-08-13T09:00:00+00:00',
  publishes: [],
  consumes: [],
};

function setUp() {
  TestBed.configureTestingModule({
    providers: [
      provideConcordatConfig({ defaultEnvironment: 'dev' }),
      provideHttpClient(),
      provideHttpClientTesting(),
    ],
  });

  return { api: TestBed.inject(ContractsApi), backend: TestBed.inject(HttpTestingController) };
}

describe('ContractsApi', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('lists the environment’s contracts', () => {
    let contracts: readonly Contract[] = [];
    const { api, backend } = setUp();
    api.listContracts('dev').subscribe((value) => (contracts = value));

    backend.expectOne('/v1/environments/dev/contracts').flush([dto]);

    expect(contracts[0]?.createdAt).toBeInstanceOf(Date);
  });

  it('gets one by name, percent-encoded', () => {
    const { api, backend } = setUp();
    api.getContract('dev', 'pay/ments').subscribe();

    backend.expectOne('/v1/environments/dev/contracts/pay%2Fments');
  });

  describe('createContract', () => {
    it('sends the name alone when the mode is left to the registry', () => {
      // Omitting it is how the registry's own default — MONITOR — is chosen. Sending a mode the
      // person never picked would make this screen the author of that decision.
      const { api, backend } = setUp();
      api.createContract('dev', 'payments', null).subscribe();

      const request = backend.expectOne('/v1/environments/dev/contracts');

      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({ name: 'payments' });
    });

    it('sends the mode when one was chosen', () => {
      const { api, backend } = setUp();
      api.createContract('dev', 'payments', 'ENFORCE').subscribe();

      expect(backend.expectOne('/v1/environments/dev/contracts').request.body).toEqual({
        name: 'payments',
        enforcement: 'ENFORCE',
      });
    });
  });

  it('puts the enforcement mode to the enforcement route', () => {
    const { api, backend } = setUp();
    api.setEnforcement('dev', 'payments', 'OFF').subscribe();

    const request = backend.expectOne('/v1/environments/dev/contracts/payments/enforcement');

    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ enforcement: 'OFF' });
  });

  describe('addPublishBinding', () => {
    const binding = {
      exchange: 'orders',
      routingKeyPattern: 'orders.*',
      subjects: [{ subject: 'orders.created', selector: 'latest' }],
      virtualHost: '',
      precedence: null,
    };

    it('posts to the publishes route', () => {
      const { api, backend } = setUp();
      api.addPublishBinding('dev', 'payments', binding).subscribe();

      const request = backend.expectOne('/v1/environments/dev/contracts/payments/publishes');

      expect(request.request.method).toBe('POST');
    });

    it('leaves a blank virtual host and a missing precedence off the body', () => {
      // The registry's default vhost is `/`; an empty string is a virtual host named nothing.
      const { api, backend } = setUp();
      api.addPublishBinding('dev', 'payments', binding).subscribe();

      expect(
        backend.expectOne('/v1/environments/dev/contracts/payments/publishes').request.body,
      ).toEqual({
        exchange: 'orders',
        routingKeyPattern: 'orders.*',
        subjects: [{ subject: 'orders.created', selector: 'latest' }],
      });
    });

    it('sends them when given, trimmed', () => {
      const { api, backend } = setUp();
      api
        .addPublishBinding('dev', 'payments', { ...binding, virtualHost: ' /prod ', precedence: 0 })
        .subscribe();

      const body = backend.expectOne('/v1/environments/dev/contracts/payments/publishes').request
        .body;

      expect(body.virtualHost).toBe('/prod');
      // Zero is a precedence. `if (precedence)` would drop it.
      expect(body.precedence).toBe(0);
    });
  });

  describe('addConsumeBinding', () => {
    it('posts to the consumes route with the queue and subjects', () => {
      const { api, backend } = setUp();
      api
        .addConsumeBinding('dev', 'payments', {
          queue: 'billing.orders',
          subjects: [{ subject: 'orders.created', selector: '2' }],
          virtualHost: '',
        })
        .subscribe();

      const request = backend.expectOne('/v1/environments/dev/contracts/payments/consumes');

      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({
        queue: 'billing.orders',
        subjects: [{ subject: 'orders.created', selector: '2' }],
      });
    });
  });
});
