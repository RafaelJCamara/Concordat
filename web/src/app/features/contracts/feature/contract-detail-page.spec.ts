import { signal, type WritableSignal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';
import { SessionStore } from '../../../core/auth/session-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { Contract } from '../../../domain/contracts/contract';
import type { Scope } from '../../../domain/identity/scope';
import { ContractDetailStore, type ContractAction } from '../application/contract-detail-store';
import { ContractDetailPage } from './contract-detail-page';

// The screen's decisions: that every control is absent without contract:write, that ENFORCE
// takes two clicks and the safe direction takes one, that a refusal sits under the form that
// caused it, and that a malformed subjects field is stopped before anything is sent.

function contract(overrides: Partial<Contract> = {}): Contract {
  return {
    name: 'payments',
    enforcement: 'MONITOR',
    createdAt: new Date('2026-08-13T09:00:00Z'),
    publishes: [],
    consumes: [],
    ...overrides,
  };
}

interface Harness {
  fixture: ComponentFixture<ContractDetailPage>;
  host: HTMLElement;
  setEnforcement: ReturnType<typeof vi.fn>;
  addPublishBinding: ReturnType<typeof vi.fn>;
  addConsumeBinding: ReturnType<typeof vi.fn>;
  lastSaved: WritableSignal<ContractAction | null>;
}

function render(
  state: {
    contract?: Contract | null;
    loading?: boolean;
    error?: ConcordatError | null;
    saving?: ContractAction | null;
    actionErrors?: Partial<Record<ContractAction, ConcordatError>>;
    lastSaved?: ContractAction | null;
  } = {},
  scopes: readonly Scope[] = ['contract:write'],
): Harness {
  const setEnforcement = vi.fn();
  const addPublishBinding = vi.fn();
  const addConsumeBinding = vi.fn();
  const lastSaved = signal(state.lastSaved ?? null);

  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });

  TestBed.overrideComponent(ContractDetailPage, {
    set: {
      providers: [
        {
          provide: ContractDetailStore,
          useValue: {
            contract: signal('contract' in state ? state.contract : contract()),
            loading: signal(state.loading ?? false),
            error: signal(state.error ?? null),
            saving: signal(state.saving ?? null),
            actionErrors: signal(state.actionErrors ?? {}),
            lastSaved,
            load: () => {},
            setEnforcement,
            addPublishBinding,
            addConsumeBinding,
          },
        },
      ],
    },
  });

  TestBed.inject(SessionStore).signIn({ credential: 'c', actor: 'alice', scopes });

  const fixture = TestBed.createComponent(ContractDetailPage);
  fixture.componentRef.setInput('name', 'payments');
  fixture.detectChanges();

  return {
    fixture,
    host: fixture.nativeElement as HTMLElement,
    setEnforcement,
    addPublishBinding,
    addConsumeBinding,
    lastSaved,
  };
}

function fill(host: HTMLElement, id: string, value: string): void {
  const element = host.querySelector(`#${id}`) as HTMLInputElement | HTMLSelectElement;
  element.value = value;
  element.dispatchEvent(new Event(element instanceof HTMLSelectElement ? 'change' : 'input'));
}

function button(host: HTMLElement, text: string): HTMLButtonElement | undefined {
  return [...host.querySelectorAll('button')].find((b) => b.textContent?.trim() === text);
}

function submit(host: HTMLElement, formLabelledBy: string): void {
  host
    .querySelector(`form[aria-labelledby="${formLabelledBy}"]`)!
    .dispatchEvent(new Event('submit'));
}

describe('ContractDetailPage', () => {
  it('names the contract and its mode, and links back to the list', () => {
    const { host } = render();

    expect(host.querySelector('h1')?.textContent).toContain('payments');
    expect(host.textContent).toContain('MONITOR');
    expect(host.querySelector('a[href="/contracts"]')).not.toBeNull();
  });

  it('shows the bindings', () => {
    const { host } = render({
      contract: contract({
        consumes: [{ queue: 'billing.orders', brokerId: null, virtualHost: '/', subjects: [] }],
      }),
    });

    expect(host.textContent).toContain('billing.orders');
  });

  it('shows a skeleton while loading', () => {
    expect(
      render({ loading: true, contract: null }).host.querySelector('[aria-busy="true"]'),
    ).not.toBeNull();
  });

  it('renders the registry’s refusal when there is no contract', () => {
    const { host } = render({
      contract: null,
      error: new ConcordatError({
        status: 404,
        code: 'contract_not_found',
        detail: "No contract 'payments'.",
      }),
    });

    expect(host.textContent).toContain('Could not load this contract');
    expect(host.textContent).toContain("No contract 'payments'.");
  });

  describe('who may change it', () => {
    it('offers the controls to someone with contract:write', () => {
      const { host } = render();

      expect(host.querySelector('#enforcement-mode')).not.toBeNull();
      expect(host.querySelector('#publish-exchange')).not.toBeNull();
      expect(host.querySelector('#consume-queue')).not.toBeNull();
    });

    it('omits every one for a reader, who can still read the contract (ADR-018)', () => {
      const { host } = render({}, ['subject:read', 'contract:read']);

      expect(host.querySelector('#enforcement-mode')).toBeNull();
      expect(host.querySelector('#publish-exchange')).toBeNull();
      expect(host.querySelector('#consume-queue')).toBeNull();
      expect(host.querySelector('h1')?.textContent).toContain('payments');
    });

    it('omits them for a subject admin, whose scope is a different resource’s', () => {
      const { host } = render({}, ['subject:admin']);

      expect(host.querySelector('#enforcement-mode')).toBeNull();
    });
  });

  describe('enforcement', () => {
    it('offers no change until the selector moves off the current mode', () => {
      const { host } = render();

      expect(button(host, 'Change to OFF')).toBeUndefined();
      expect(button(host, 'Change to ENFORCE')).toBeUndefined();
    });

    it('applies the safe direction in one click', () => {
      const { fixture, host, setEnforcement } = render();

      fill(host, 'enforcement-mode', 'OFF');
      fixture.detectChanges();
      button(host, 'Change to OFF')!.click();

      expect(setEnforcement).toHaveBeenCalledWith('OFF');
    });

    it('takes two clicks to move to ENFORCE, and says what it does', () => {
      const { fixture, host, setEnforcement } = render();

      fill(host, 'enforcement-mode', 'ENFORCE');
      fixture.detectChanges();
      button(host, 'Change to ENFORCE')!.click();
      fixture.detectChanges();

      expect(setEnforcement).not.toHaveBeenCalled();
      expect(host.textContent).toContain('Every service that resolves one of these routes');
      expect(host.textContent).toContain('refuse a message that violates it');

      button(host, 'Confirm')!.click();

      expect(setEnforcement).toHaveBeenCalledWith('ENFORCE');
    });

    it('sends nothing when the confirmation is cancelled', () => {
      const { fixture, host, setEnforcement } = render();

      fill(host, 'enforcement-mode', 'ENFORCE');
      fixture.detectChanges();
      button(host, 'Change to ENFORCE')!.click();
      fixture.detectChanges();
      button(host, 'Cancel')!.click();
      fixture.detectChanges();

      expect(setEnforcement).not.toHaveBeenCalled();
      expect(button(host, 'Change to ENFORCE')).toBeUndefined();
    });

    it('shows the registry’s refusal under the control', () => {
      const { host } = render({
        actionErrors: {
          enforcement: new ConcordatError({
            status: 403,
            code: 'insufficient_scope',
            detail: 'nope',
          }),
        },
      });

      expect(host.textContent).toContain('Could not change the mode');
      expect(host.textContent).toContain('nope');
    });
  });

  describe('adding a publish binding', () => {
    function fillPublish(host: HTMLElement, subjects = 'orders.created@>=1'): void {
      fill(host, 'publish-exchange', ' orders ');
      fill(host, 'publish-pattern', 'orders.*');
      fill(host, 'publish-subjects', subjects);
    }

    it('sends the parsed subjects and trimmed fields', () => {
      const { fixture, host, addPublishBinding } = render();

      fillPublish(host);
      fixture.detectChanges();
      submit(host, 'add-publish-heading');

      expect(addPublishBinding).toHaveBeenCalledWith({
        exchange: 'orders',
        routingKeyPattern: 'orders.*',
        subjects: [{ subject: 'orders.created', selector: '>=1' }],
        virtualHost: '',
        precedence: null,
      });
    });

    it('passes a precedence of zero, which is one', () => {
      const { fixture, host, addPublishBinding } = render();

      fillPublish(host);
      fill(host, 'publish-precedence', '0');
      fixture.detectChanges();
      submit(host, 'add-publish-heading');

      expect(addPublishBinding.mock.calls[0]![0].precedence).toBe(0);
    });

    it('stops a malformed subject before anything is sent, and says which part is wrong', () => {
      const { fixture, host, addPublishBinding } = render();

      fillPublish(host, 'orders.created@');
      fixture.detectChanges();
      submit(host, 'add-publish-heading');
      fixture.detectChanges();

      expect(addPublishBinding).not.toHaveBeenCalled();
      expect(host.querySelector('[role="alert"]')?.textContent).toContain('after it');
    });

    it('stops an incomplete form', () => {
      const { fixture, host, addPublishBinding } = render();

      fill(host, 'publish-exchange', 'orders');
      fixture.detectChanges();
      submit(host, 'add-publish-heading');
      fixture.detectChanges();

      expect(addPublishBinding).not.toHaveBeenCalled();
      expect(host.querySelector('[role="alert"]')?.textContent).toContain('required');
    });

    it('shows the registry’s refusal under this form, and not the other', () => {
      // An overlap is decided by intersecting patterns, which the browser cannot see. The
      // refusal belongs under the form that caused it and must not appear under the other.
      const { host } = render({
        actionErrors: {
          publish: new ConcordatError({
            status: 409,
            code: 'binding_overlaps',
            detail: "'orders.*' overlaps '*.created'.",
          }),
        },
      });

      const publishForm = host.querySelector('form[aria-labelledby="add-publish-heading"]')!;
      const consumeForm = host.querySelector('form[aria-labelledby="add-consume-heading"]')!;

      expect(publishForm.textContent).toContain("'orders.*' overlaps '*.created'.");
      expect(consumeForm.textContent).not.toContain('overlaps');
    });

    it('disables both add buttons while any change is in flight', () => {
      const { host } = render({ saving: 'enforcement' });

      expect(button(host, 'Add publish binding')!.disabled).toBe(true);
      expect(button(host, 'Add consume binding')!.disabled).toBe(true);
    });

    it('empties the form once the binding is stored', () => {
      const { fixture, host, lastSaved } = render();

      fillPublish(host);
      fixture.detectChanges();
      lastSaved.set('publish');
      fixture.detectChanges();

      expect((host.querySelector('#publish-exchange') as HTMLInputElement).value).toBe('');
    });

    it('keeps what was typed when the registry refused it', () => {
      const { fixture, host } = render({
        actionErrors: { publish: new ConcordatError({ status: 409, code: 'x', detail: 'no' }) },
      });

      fillPublish(host);
      fixture.detectChanges();

      expect((host.querySelector('#publish-exchange') as HTMLInputElement).value).toContain(
        'orders',
      );
    });
  });

  describe('adding a consume binding', () => {
    it('sends the queue and parsed subjects', () => {
      const { fixture, host, addConsumeBinding } = render();

      fill(host, 'consume-queue', 'billing.orders');
      fill(host, 'consume-subjects', 'orders.created, payments.taken@2');
      fixture.detectChanges();
      submit(host, 'add-consume-heading');

      expect(addConsumeBinding).toHaveBeenCalledWith({
        queue: 'billing.orders',
        subjects: [
          { subject: 'orders.created', selector: 'latest' },
          { subject: 'payments.taken', selector: '2' },
        ],
        virtualHost: '',
      });
    });

    it('stops a form with no subject', () => {
      const { fixture, host, addConsumeBinding } = render();

      fill(host, 'consume-queue', 'billing.orders');
      fixture.detectChanges();
      submit(host, 'add-consume-heading');
      fixture.detectChanges();

      expect(addConsumeBinding).not.toHaveBeenCalled();
    });
  });

  it('says what just succeeded', () => {
    const { host } = render({ lastSaved: 'publish' });

    expect(host.querySelector('[role="status"]')?.textContent).toContain('Publish binding added');
  });
});
