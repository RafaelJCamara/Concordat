import { describe, expect, it } from 'vitest';
import { checkBindingEdit } from './binding-edit';

// Only the shape is checked in the browser. Whether a change would leave two overlapping bindings
// carrying different subjects with no precedence between them is the registry's to say.

const current = {
  subjects: [
    { subject: 'orders.created', selector: 'latest' },
    { subject: 'orders.updated', selector: '2' },
  ],
  precedence: 5,
};

describe('checkBindingEdit', () => {
  it('sends a changed precedence with the same subjects', () => {
    const check = checkBindingEdit(
      current,
      { subjects: 'orders.created@latest, orders.updated@2', precedence: 6 },
      true,
    );

    expect(check.finding).toBeNull();
    expect(check.send).toEqual({ subjects: current.subjects, precedence: 6 });
  });

  it('sends a changed subject list with the same precedence', () => {
    const check = checkBindingEdit(current, { subjects: 'orders.created', precedence: 5 }, true);

    expect(check.send).toEqual({
      subjects: [{ subject: 'orders.created', selector: 'latest' }],
      precedence: 5,
    });
  });

  it('sends a cleared precedence as null, which is how one is removed', () => {
    const check = checkBindingEdit(
      current,
      { subjects: 'orders.created@latest, orders.updated@2', precedence: null },
      true,
    );

    expect(check.send?.precedence).toBeNull();
  });

  it('sends a precedence of zero, which is a precedence', () => {
    const check = checkBindingEdit(
      { ...current, precedence: null },
      { subjects: 'orders.created@latest, orders.updated@2', precedence: 0 },
      true,
    );

    expect(check.send?.precedence).toBe(0);
  });

  it('has nothing to send for what the binding already carries, and says so as advice', () => {
    const check = checkBindingEdit(
      current,
      { subjects: 'orders.created@latest, orders.updated@2', precedence: 5 },
      true,
    );

    expect(check.send).toBeNull();
    expect(check.finding).toEqual({
      severity: 'advice',
      message: 'That is what this binding carries now.',
    });
  });

  it('reads a bare subject as latest, so typing it the short way is not a change', () => {
    const check = checkBindingEdit(
      current,
      { subjects: 'orders.created orders.updated@2', precedence: 5 },
      true,
    );

    expect(check.send).toBeNull();
  });

  it('counts the same subjects in another order as a change, because the order is stored', () => {
    const check = checkBindingEdit(
      current,
      { subjects: 'orders.updated@2, orders.created', precedence: 5 },
      true,
    );

    expect(check.send?.subjects.map((s) => s.subject)).toEqual([
      'orders.updated',
      'orders.created',
    ]);
  });

  it.each(['', '   '])('refuses %j, which names no subject', (text) => {
    const check = checkBindingEdit(current, { subjects: text, precedence: 5 }, true);

    expect(check.send).toBeNull();
    expect(check.finding?.severity).toBe('refusal');
    expect(check.finding?.message).toBe('Name at least one subject.');
  });

  it('refuses a list that is only separators, with the reason for a malformed one', () => {
    const check = checkBindingEdit(current, { subjects: ',', precedence: 5 }, true);

    expect(check.send).toBeNull();
    expect(check.finding?.severity).toBe('refusal');
  });

  it('refuses a subject with an @ and nothing after it, rather than reading it as latest', () => {
    const check = checkBindingEdit(current, { subjects: 'orders.created@', precedence: 5 }, true);

    expect(check.finding?.severity).toBe('refusal');
    expect(check.finding?.message).toContain('orders.created@2');
  });

  it('refuses a precedence that is not a whole number', () => {
    const check = checkBindingEdit(current, { subjects: 'orders.created', precedence: 1.5 }, true);

    expect(check.send).toBeNull();
    expect(check.finding).toEqual({
      severity: 'refusal',
      message: 'A precedence is a whole number.',
    });
  });

  it('ignores the precedence for a binding that has none to give (a consume binding)', () => {
    const consume = { subjects: current.subjects, precedence: null };

    const same = checkBindingEdit(
      consume,
      { subjects: 'orders.created@latest, orders.updated@2', precedence: 9 },
      false,
    );
    const changed = checkBindingEdit(consume, { subjects: 'orders.created', precedence: 9 }, false);

    expect(same.send).toBeNull();
    expect(changed.send?.precedence).toBeNull();
  });
});
