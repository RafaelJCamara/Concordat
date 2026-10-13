import { describe, expect, it } from 'vitest';
import { toAuditEntry } from './audit-dtos';

// Strict about the timestamp and lenient about the action, for the same reason in opposite
// directions: a date that does not parse breaks the one list whose order is the point, while an
// action this build has never heard of is still a perfectly good label.

const dto = {
  at: '2026-08-13T09:00:00+00:00',
  action: 'VERSION_APPROVED',
  actor: 'alice',
  target: 'orders.created',
  detail: 'version 2',
};

describe('toAuditEntry', () => {
  it('maps every field', () => {
    expect(toAuditEntry(dto)).toEqual({
      at: new Date('2026-08-13T09:00:00Z'),
      action: 'VERSION_APPROVED',
      actor: 'alice',
      target: 'orders.created',
      detail: 'version 2',
    });
  });

  it('renders an action it has never seen, rather than failing the page', () => {
    // The opposite of the status and certainty guards, deliberately: those mislabel a state
    // when wrong, and this is only a label on a record. A log that cannot display the newest
    // kind of entry hides exactly what somebody opened it to find.
    expect(toAuditEntry({ ...dto, action: 'TENANT_SUSPENDED' }).action).toBe('TENANT_SUSPENDED');
  });

  it('keeps a null detail null', () => {
    expect(toAuditEntry({ ...dto, detail: null }).detail).toBeNull();
  });

  it('refuses a timestamp that is not a date', () => {
    expect(() => toAuditEntry({ ...dto, at: 'yesterday-ish' })).toThrow(/yesterday-ish/);
  });
});
