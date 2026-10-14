import { signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';
import { SessionStore } from '../../../core/auth/session-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { Contract } from '../../../domain/contracts/contract';
import type { Scope } from '../../../domain/identity/scope';
import { ContractListStore } from '../application/contract-list-store';
import { ContractsPage } from './contracts-page';

// The screen's decisions: that creation is absent for someone who cannot do it, that leaving
// the mode on "default" sends none, and that a created contract empties the form.

function contract(name: string): Contract {
  return {
    name,
    enforcement: 'MONITOR',
    createdAt: new Date('2026-08-13T09:00:00Z'),
    publishes: [
      {
        id: 'p1',
        exchange: 'orders',
        routingKeyPattern: 'orders.*',
        brokerId: null,
        virtualHost: '/',
        precedence: null,
        subjects: [],
      },
    ],
    consumes: [],
  };
}

function render(
  state: {
    contracts?: readonly Contract[];
    loading?: boolean;
    isEmpty?: boolean;
    error?: ConcordatError | null;
    creating?: boolean;
    createError?: ConcordatError | null;
    created?: Contract | null;
  } = {},
  scopes: readonly Scope[] = ['contract:write'],
): {
  fixture: ComponentFixture<ContractsPage>;
  host: HTMLElement;
  create: ReturnType<typeof vi.fn>;
} {
  const create = vi.fn();

  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });

  TestBed.overrideComponent(ContractsPage, {
    set: {
      providers: [
        {
          provide: ContractListStore,
          useValue: {
            contracts: signal(state.contracts ?? []),
            loading: signal(state.loading ?? false),
            isEmpty: signal(state.isEmpty ?? false),
            error: signal(state.error ?? null),
            creating: signal(state.creating ?? false),
            createError: signal(state.createError ?? null),
            created: signal(state.created ?? null),
            load: () => {},
            create,
          },
        },
      ],
    },
  });

  TestBed.inject(SessionStore).signIn({ credential: 'c', actor: 'alice', scopes });

  const fixture = TestBed.createComponent(ContractsPage);
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, create };
}

function fill(host: HTMLElement, id: string, value: string): void {
  const element = host.querySelector(`#${id}`) as HTMLInputElement | HTMLSelectElement;
  element.value = value;
  element.dispatchEvent(new Event(element instanceof HTMLSelectElement ? 'change' : 'input'));
}

describe('ContractsPage', () => {
  it('lists each contract, linked, with its mode and binding counts', () => {
    const { host } = render({ contracts: [contract('payments')] });

    expect(host.querySelector('a[href="/contracts/payments"]')).not.toBeNull();
    expect(host.textContent).toContain('MONITOR');
    expect(host.textContent).toContain('1 publish');
    expect(host.textContent).toContain('0 consume');
  });

  it('says what an empty list means for a service', () => {
    const { host } = render({ isEmpty: true });

    expect(host.textContent).toContain('No contracts yet');
    expect(host.textContent).toContain('ungoverned');
  });

  it('shows a skeleton while loading', () => {
    expect(render({ loading: true }).host.querySelector('[aria-busy="true"]')).not.toBeNull();
  });

  it('renders a failed load with a retry', () => {
    const { host } = render({
      error: new ConcordatError({ status: 500, code: 'x', detail: 'The registry is down.' }),
    });

    expect(host.textContent).toContain('Could not load contracts');
    expect(host.textContent).toContain('The registry is down.');
  });

  describe('creating', () => {
    it('is offered to someone with contract:write', () => {
      expect(render().host.querySelector('#contract-name')).not.toBeNull();
    });

    it.each([
      ['a reader', ['subject:read']],
      ['a schema writer', ['subject:write', 'subject:admin']],
    ] as const)('is absent for %s, not disabled (ADR-018)', (_who, scopes) => {
      const { host } = render({}, scopes as readonly Scope[]);

      expect(host.querySelector('#contract-name')).toBeNull();
      expect(host.textContent).not.toContain('Create');
    });

    it('sends no mode when left on default, so the registry chooses MONITOR', () => {
      const { fixture, host, create } = render();

      fill(host, 'contract-name', 'payments');
      fixture.detectChanges();
      host.querySelector('form')!.dispatchEvent(new Event('submit'));

      expect(create).toHaveBeenCalledWith({ name: 'payments', enforcement: null });
    });

    it('sends the mode when one was chosen', () => {
      const { fixture, host, create } = render();

      fill(host, 'contract-name', 'payments');
      fill(host, 'contract-enforcement', 'ENFORCE');
      fixture.detectChanges();
      host.querySelector('form')!.dispatchEvent(new Event('submit'));

      expect(create).toHaveBeenCalledWith({ name: 'payments', enforcement: 'ENFORCE' });
    });

    it('trims the name', () => {
      const { fixture, host, create } = render();

      fill(host, 'contract-name', '  payments  ');
      fixture.detectChanges();
      host.querySelector('form')!.dispatchEvent(new Event('submit'));

      expect(create).toHaveBeenCalledWith({ name: 'payments', enforcement: null });
    });

    it('sends nothing for a blank name, and disables the button', () => {
      const { fixture, host, create } = render();

      fill(host, 'contract-name', '   ');
      fixture.detectChanges();
      host.querySelector('form')!.dispatchEvent(new Event('submit'));

      expect(create).not.toHaveBeenCalled();
      expect(host.querySelector<HTMLButtonElement>('button[type="submit"]')!.disabled).toBe(true);
    });

    it('disables the button while a create is in flight', () => {
      const { fixture, host } = render({ creating: true });
      fill(host, 'contract-name', 'payments');
      fixture.detectChanges();

      expect(host.querySelector<HTMLButtonElement>('button[type="submit"]')!.disabled).toBe(true);
    });

    it('shows the registry’s refusal under the form', () => {
      const { host } = render({
        createError: new ConcordatError({
          status: 409,
          code: 'contract_already_exists',
          detail: "A contract named 'payments' already exists.",
        }),
      });

      expect(host.textContent).toContain('Could not create the contract');
      expect(host.textContent).toContain("A contract named 'payments' already exists.");
    });

    it('says what was created, and that it governs nothing yet', () => {
      const { host } = render({ created: contract('payments') });

      const status = host.querySelector('[role="status"]');

      expect(status?.textContent).toContain('Created');
      expect(status?.textContent).toContain('payments');
      expect(status?.textContent).toContain('governs nothing until it has a binding');
    });

    it('empties the form once a contract was created', () => {
      const { fixture, host } = render();
      fill(host, 'contract-name', 'payments');
      fixture.detectChanges();

      const store = fixture.debugElement.injector.get(ContractListStore) as unknown as {
        created: ReturnType<typeof signal<Contract | null>>;
      };
      store.created.set(contract('payments'));
      fixture.detectChanges();

      expect((host.querySelector('#contract-name') as HTMLInputElement).value).toBe('');
    });
  });
});
