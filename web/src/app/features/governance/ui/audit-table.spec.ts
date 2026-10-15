import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { AuditEntry } from '../../../domain/governance/audit';
import { AuditTable } from './audit-table';

// The two properties worth pinning: the exact time sits beside the relative one, because an
// audit log is where the next question is always "when exactly"; and an action this build has
// never heard of renders as itself.

function entry(overrides: Partial<AuditEntry> = {}): AuditEntry {
  return {
    at: new Date('2026-08-13T09:00:00Z'),
    action: 'VERSION_APPROVED',
    actor: 'alice@example.com',
    target: 'orders.created',
    detail: 'version 2',
    ...overrides,
  };
}

describe('AuditTable', () => {
  let fixture: ComponentFixture<AuditTable>;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [AuditTable] });
    fixture = TestBed.createComponent(AuditTable);
  });

  function render(entries: readonly AuditEntry[]): HTMLElement {
    fixture.componentRef.setInput('entries', entries);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  it('renders who did what to which thing', () => {
    const host = render([entry()]);

    expect(host.textContent).toContain('alice@example.com');
    expect(host.textContent).toContain('VERSION_APPROVED');
    expect(host.textContent).toContain('orders.created');
    expect(host.textContent).toContain('version 2');
  });

  it('puts the exact time in a title, beside the relative one', () => {
    const host = render([entry()]);

    expect(host.querySelector('td[title="2026-08-13T09:00:00.000Z"]')).not.toBeNull();
  });

  it('keeps the order it was given, which is the registry’s newest-first', () => {
    const host = render([entry({ target: 'newest' }), entry({ target: 'oldest' })]);

    const targets = [...host.querySelectorAll('tbody tr')].map((row) => row.textContent);

    expect(targets[0]).toContain('newest');
    expect(targets[1]).toContain('oldest');
  });

  it('renders an action it has never seen, unchanged', () => {
    const host = render([entry({ action: 'TENANT_SUSPENDED' })]);

    expect(host.textContent).toContain('TENANT_SUSPENDED');
  });

  it('renders a null detail as an empty cell, not the word null', () => {
    const host = render([entry({ detail: null })]);

    expect(host.textContent).not.toContain('null');
  });

  it('says in the caption that a refusal leaves no entry', () => {
    // Without it, an empty search for "who tried to approve this" reads as "nobody did".
    expect(render([entry()]).querySelector('caption')?.textContent).toContain(
      'a refused request leaves no entry',
    );
  });

  it('tones a rejection as destructive and an approval as success', () => {
    const host = render([entry({ action: 'VERSION_REJECTED' }), entry()]);

    const badges = [...host.querySelectorAll('cd-status-badge')];

    expect(badges[0]!.outerHTML).toContain('bg-destructive/10');
    expect(badges[1]!.outerHTML).toContain('bg-success/10');
  });

  it('tones a permanent deletion as destructive, as a retirement is', () => {
    const host = render([
      entry({ action: 'SUBJECT_PURGED' }),
      entry({ action: 'SUBJECT_RETIRED' }),
    ]);

    const badges = [...host.querySelectorAll('cd-status-badge')];

    expect(badges[0]!.outerHTML).toContain('bg-destructive/10');
    expect(badges[1]!.outerHTML).toContain('bg-destructive/10');
  });

  it('leaves an action it has no tone for neutral', () => {
    const host = render([entry({ action: 'TENANT_SUSPENDED' })]);

    expect(host.querySelector('cd-status-badge')!.outerHTML).toContain('bg-muted');
  });

  // ADR-036. The registry now answers a row whose action it was never taught (a rollback past a
  // change that added one) as the token it holds, so "as received" has to survive any token, not
  // only the tidy upper-snake ones. The tone table is a plain object, and a lookup in a plain
  // object finds what every object inherits: `TONES['constructor']` is `Object`, which is not a
  // tone, and the badge rendered with no colour classes at all.
  it.each(['constructor', 'toString', 'hasOwnProperty', '__proto__', 'valueOf'])(
    'tones the received token %s neutral and shows it as it came',
    (action) => {
      const host = render([entry({ action })]);

      const badge = host.querySelector('cd-status-badge')!;
      expect(badge.textContent).toContain(action);
      expect(badge.outerHTML).toContain('bg-muted');
      expect(badge.outerHTML).not.toContain('undefined');
    },
  );

  it('says on an action it does not know that it is shown as the registry sent it', () => {
    // Neutral is the only honest tone for a kind of event nobody here has read the meaning of, and
    // the one thing the badge can add is that it is not an ordinary one. Without it a destructive
    // action from a newer registry reads as the neutral background of a registry being used.
    const host = render([entry({ action: 'A_FUTURE_ACTION' })]);

    const badge = host.querySelector('cd-status-badge')!;
    expect(badge.getAttribute('title')).toBe(
      'This version of the app does not know this action. It is shown as the registry sent it.',
    );
    expect(badge.textContent).toContain('A_FUTURE_ACTION');
  });

  it('adds nothing to an action it knows', () => {
    const host = render([entry({ action: 'CONTRACT_BINDING_REMOVED' })]);

    expect(host.querySelector('cd-status-badge')!.hasAttribute('title')).toBe(false);
  });
});
