import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { BindingChange } from '../../../domain/contracts/binding-edit';
import type {
  ConsumeBinding,
  EnforcementMode,
  PublishBinding,
} from '../../../domain/contracts/contract';
import { BindingEditor, type BindingTarget } from './binding-editor';

// The editor's own decisions: it starts on what the binding carries, it blocks only on the shape
// and on a request with nothing in it, the second click can only send what the first showed, and
// each confirmation says what the registry was measured to do (ADR-035).

const publish: PublishBinding = {
  id: 'p1',
  exchange: 'orders',
  routingKeyPattern: 'orders.#',
  brokerId: null,
  virtualHost: '/',
  precedence: null,
  subjects: [{ subject: 'orders.fallback', selector: 'latest' }],
};

const consume: ConsumeBinding = {
  id: 'c1',
  queue: 'billing.orders',
  brokerId: null,
  virtualHost: '/',
  subjects: [{ subject: 'orders.created', selector: '2' }],
};

function render(
  target: BindingTarget,
  mode: 'change' | 'remove' = 'change',
  extra: { enforcement?: EnforcementMode; error?: string | null; saving?: boolean } = {},
) {
  const sent: BindingChange[] = [];
  const events = { removed: 0, cancelled: 0 };

  const fixture = TestBed.createComponent(BindingEditor);
  fixture.componentRef.setInput('target', target);
  fixture.componentRef.setInput('mode', mode);
  fixture.componentRef.setInput('enforcement', extra.enforcement ?? 'MONITOR');
  fixture.componentRef.setInput('error', extra.error ?? null);
  fixture.componentRef.setInput('saving', extra.saving ?? false);
  fixture.componentInstance.changed.subscribe((change) => sent.push(change));
  fixture.componentInstance.removed.subscribe(() => events.removed++);
  fixture.componentInstance.cancelled.subscribe(() => events.cancelled++);
  fixture.detectChanges();

  const host = fixture.nativeElement as HTMLElement;

  return {
    fixture,
    host,
    sent,
    events,
    text: () => host.textContent?.replace(/\s+/g, ' ') ?? '',
    type(id: string, value: string) {
      const element = host.querySelector(`#${id}`) as HTMLInputElement;
      element.value = value;
      element.dispatchEvent(new Event('input'));
      fixture.detectChanges();
    },
    press(label: string) {
      const found = [...host.querySelectorAll('button')].find(
        (b) => b.textContent?.trim() === label,
      );
      if (!found) throw new Error(`No button "${label}"`);
      found.click();
      fixture.detectChanges();
    },
    review: () =>
      [...host.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Review change'),
  };
}

describe('BindingEditor, changing a publish binding', () => {
  it('names the binding by its exchange and pattern', () => {
    expect(render({ kind: 'publish', binding: publish }).text()).toContain('orders / orders.#');
  });

  it('starts on what the binding carries, so changing it is an edit and not a blank form', () => {
    const { host } = render({
      kind: 'publish',
      binding: { ...publish, precedence: 4, subjects: [{ subject: 'a.b', selector: '>=2' }] },
    });

    expect((host.querySelector('#binding-subjects') as HTMLInputElement).value).toBe('a.b@>=2');
    expect((host.querySelector('#binding-precedence') as HTMLInputElement).value).toBe('4');
  });

  it('has nothing to review until something differs, and says so only after the person has touched it', () => {
    const view = render({ kind: 'publish', binding: publish });

    expect(view.review()?.disabled).toBe(true);
    expect(view.text()).not.toContain('That is what this binding carries now');

    view.type('binding-subjects', 'orders.other');
    view.type('binding-subjects', 'orders.fallback');

    expect(view.review()?.disabled).toBe(true);
    expect(view.text()).toContain('That is what this binding carries now');
  });

  it('blocks a list that names no subject, with the reason', () => {
    const view = render({ kind: 'publish', binding: publish });

    view.type('binding-subjects', '  ');

    expect(view.review()?.disabled).toBe(true);
    expect(view.text()).toContain('Name at least one subject.');
  });

  it('blocks a precedence that is not a whole number', () => {
    const view = render({ kind: 'publish', binding: publish });

    view.type('binding-precedence', '1.5');

    expect(view.review()?.disabled).toBe(true);
    expect(view.text()).toContain('A precedence is a whole number.');
  });

  it('asks first, and sends nothing until the second click', () => {
    const view = render({ kind: 'publish', binding: publish });
    view.type('binding-precedence', '1');

    view.press('Review change');

    expect(view.text()).toContain('Change the binding of orders / orders.#?');
    expect(view.sent).toEqual([]);

    view.press('Yes, change the binding');

    expect(view.sent).toEqual([{ subjects: publish.subjects, precedence: 1 }]);
  });

  it('sends the subjects and a cleared precedence as null', () => {
    const view = render({ kind: 'publish', binding: { ...publish, precedence: 7 } });
    view.type('binding-precedence', '');
    view.type('binding-subjects', 'orders.a@2, orders.b');

    view.press('Review change');
    view.press('Yes, change the binding');

    expect(view.sent).toEqual([
      {
        subjects: [
          { subject: 'orders.a', selector: '2' },
          { subject: 'orders.b', selector: 'latest' },
        ],
        precedence: null,
      },
    ]);
  });

  it('withdraws the confirmation when the form changes after the first click', () => {
    // So the second click cannot send what the first did not show.
    const view = render({ kind: 'publish', binding: publish });
    view.type('binding-precedence', '1');
    view.press('Review change');

    view.type('binding-precedence', '2');

    expect(view.text()).not.toContain('Yes, change the binding');
    expect(view.sent).toEqual([]);
  });

  it('says what the registry does about a change, as it was measured', () => {
    const view = render({ kind: 'publish', binding: publish });
    view.type('binding-precedence', '1');
    view.press('Review change');
    const text = view.text();

    expect(text).toContain('Only the subjects and the precedence change.');
    expect(text).toContain(
      'The exchange, the pattern and the virtual host are what the binding is',
    );
    expect(text).toContain('from the next resolution');
    expect(text).toContain('keeps its answer until its cache expires');
    expect(text).toContain('ContractTtl');
    expect(text).toContain('refuses a change that would leave this binding overlapping');
    expect(text).toContain('CONTRACT_BINDING_CHANGED');
    expect(text).toContain('Nobody is notified.');
  });

  it('shows the registry’s refusal in its own words and keeps the form', () => {
    const view = render({ kind: 'publish', binding: publish }, 'change', {
      error: "'orders.created' overlaps 'orders.#'",
    });

    expect(view.text()).toContain('The registry refused this change');
    expect(view.text()).toContain("'orders.created' overlaps 'orders.#'");
    expect(view.host.querySelector('#binding-subjects')).not.toBeNull();
  });

  it('goes back to the form, with what was typed, when the registry refuses', () => {
    // Left on the confirmation, the second click would send the same refused request again, and
    // there would be no way to edit it. A refusal is the registry saying "not that": the next move
    // is to change it.
    const view = render({ kind: 'publish', binding: publish });
    view.type('binding-precedence', '1');
    view.press('Review change');
    view.press('Yes, change the binding');

    view.fixture.componentRef.setInput('saving', true);
    view.fixture.detectChanges();
    view.fixture.componentRef.setInput('saving', false);
    view.fixture.componentRef.setInput('error', "'orders.created' overlaps 'orders.#'");
    view.fixture.detectChanges();

    expect(view.text()).not.toContain('Yes, change the binding');
    expect(view.review()?.disabled).toBe(false);
    expect((view.host.querySelector('#binding-precedence') as HTMLInputElement).value).toBe('1');
    expect(view.text()).toContain("'orders.created' overlaps 'orders.#'");
  });

  it('stays on the confirmation while the registry has not answered', () => {
    const view = render({ kind: 'publish', binding: publish });
    view.type('binding-precedence', '1');
    view.press('Review change');
    view.press('Yes, change the binding');

    view.fixture.componentRef.setInput('saving', true);
    view.fixture.detectChanges();

    expect(view.text()).toContain('Changing…');
  });

  it('cancels', () => {
    const view = render({ kind: 'publish', binding: publish });

    view.press('Cancel');

    expect(view.events.cancelled).toBe(1);
  });
});

describe('BindingEditor, changing a consume binding', () => {
  it('has no precedence, which a consume binding does not have', () => {
    const view = render({ kind: 'consume', binding: consume });

    expect(view.host.querySelector('#binding-precedence')).toBeNull();
    expect(view.text()).toContain('billing.orders');
  });

  it('sends only subjects, with a null precedence', () => {
    const view = render({ kind: 'consume', binding: consume });
    view.type('binding-subjects', 'orders.created@3');

    view.press('Review change');
    expect(view.text()).toContain('The queue and the virtual host are what the binding is');
    view.press('Yes, change the binding');

    expect(view.sent).toEqual([
      { subjects: [{ subject: 'orders.created', selector: '3' }], precedence: null },
    ]);
  });
});

describe('BindingEditor, removing', () => {
  it('goes straight to the question, with no form to fill', () => {
    const view = render({ kind: 'publish', binding: publish }, 'remove');

    expect(view.text()).toContain('Remove the binding of orders / orders.#?');
    expect(view.host.querySelector('#binding-subjects')).toBeNull();
  });

  it('says what a removal does to the route, and that it can be added again', () => {
    const text = render({ kind: 'publish', binding: publish }, 'remove').text();

    expect(text).toContain(
      'Routes matching orders.# on orders stop being governed by this binding.',
    );
    expect(text).toContain(
      'If nothing else governs them (another binding of this contract, or another contract)',
    );
    expect(text).toContain('keeps its answer until its cache expires');
    expect(text).toContain(
      'It cannot be undone, but a binding with the same details can be added again',
    );
    expect(text).toContain('CONTRACT_BINDING_REMOVED');
    expect(text).toContain('Nobody is notified.');
  });

  it('says an enforcing contract stops refusing on those routes, and only when it enforces', () => {
    const enforcing = render({ kind: 'publish', binding: publish }, 'remove', {
      enforcement: 'ENFORCE',
    }).text();
    const monitoring = render({ kind: 'publish', binding: publish }, 'remove').text();

    expect(enforcing).toContain('This contract enforces');
    expect(monitoring).not.toContain('This contract enforces');
  });

  it('says what removing a consume binding does to its queue', () => {
    const text = render({ kind: 'consume', binding: consume }, 'remove').text();

    expect(text).toContain('billing.orders is no longer expected to receive these subjects');
  });

  it('removes on the second click and goes back on "Keep it"', () => {
    const removed = render({ kind: 'publish', binding: publish }, 'remove');
    removed.press('Yes, remove the binding');

    expect(removed.events.removed).toBe(1);

    const kept = render({ kind: 'publish', binding: publish }, 'remove');
    kept.press('Keep it');

    expect(kept.events.cancelled).toBe(1);
    expect(kept.events.removed).toBe(0);
  });

  it('shows why the registry could not remove it', () => {
    const text = render({ kind: 'publish', binding: publish }, 'remove', {
      error: "This contract has no publish binding 'x'.",
    }).text();

    expect(text).toContain('Could not remove it');
    expect(text).toContain("This contract has no publish binding 'x'.");
  });
});
