import { describe, expect, it } from 'vitest';
import { toContract, type ContractDto } from './contract-dtos';

// The mapping is where a wire change shows up first. What is not obvious from the types is
// that the enforcement guard is strict: a contract shown as one thing that is in fact another
// is a contract somebody believes is not blocking.

const dto: ContractDto = {
  name: 'payments',
  enforcement: 'MONITOR',
  createdAt: '2026-08-13T09:00:00+00:00',
  publishes: [
    {
      exchange: 'orders',
      routingKeyPattern: 'orders.*',
      brokerId: null,
      virtualHost: '/',
      precedence: 5,
      subjects: [{ subject: 'orders.created', selector: '>=1' }],
    },
  ],
  consumes: [
    {
      queue: 'billing.orders',
      brokerId: '0a0a0a0a-0a0a-0a0a-0a0a-0a0a0a0a0a0a',
      virtualHost: '/',
      subjects: [{ subject: 'orders.created', selector: 'latest' }],
    },
  ],
};

describe('toContract', () => {
  it('maps the contract and both kinds of binding', () => {
    const contract = toContract(dto);

    expect(contract.name).toBe('payments');
    expect(contract.enforcement).toBe('MONITOR');
    expect(contract.publishes[0]).toEqual({
      exchange: 'orders',
      routingKeyPattern: 'orders.*',
      brokerId: null,
      virtualHost: '/',
      precedence: 5,
      subjects: [{ subject: 'orders.created', selector: '>=1' }],
    });
    expect(contract.consumes[0]?.queue).toBe('billing.orders');
  });

  it('parses the creation timestamp', () => {
    expect(toContract(dto).createdAt.toISOString()).toBe('2026-08-13T09:00:00.000Z');
  });

  it('keeps a null precedence null, which is what makes an overlap a conflict', () => {
    const contract = toContract({
      ...dto,
      publishes: [{ ...dto.publishes[0]!, precedence: null }],
    });

    expect(contract.publishes[0]?.precedence).toBeNull();
  });

  it('keeps a selector as the text the registry sent', () => {
    expect(toContract(dto).publishes[0]?.subjects[0]?.selector).toBe('>=1');
  });

  it.each(['OFF', 'MONITOR', 'ENFORCE'])('accepts the enforcement mode %s', (mode) => {
    expect(toContract({ ...dto, enforcement: mode }).enforcement).toBe(mode);
  });

  it('refuses an enforcement mode it does not recognise, rather than showing it as something else', () => {
    expect(() => toContract({ ...dto, enforcement: 'AUDIT' })).toThrow(/AUDIT/);
  });

  it('refuses a timestamp that is not a date', () => {
    expect(() => toContract({ ...dto, createdAt: 'sometime' })).toThrow(/sometime/);
  });
});
