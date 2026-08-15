import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { SchemaVersion, Subject } from '../../../domain/registry/subject';
import { SubjectCard } from './subject-card';

// Presentational, so these are about what a reader can see. The card and `SubjectTable` show
// the same subject in two shapes, and the decisions they must agree on — `latest` follows the
// approval gate, an absent version is an em dash — are asserted in both places on purpose. A
// card that disagreed with the table about which version is live would be a worse bug than
// either being wrong alone.

function version(overrides: Partial<SchemaVersion> & { ordinal: number }): SchemaVersion {
  return {
    schemaId: '0123456789abcdef0123456789abcdef',
    semanticVersion: null,
    status: 'ACTIVE',
    changelog: null,
    registeredAt: new Date('2026-08-13T09:00:00.000Z'),
    registeredBy: 'ci',
    deprecated: false,
    ...overrides,
  };
}

function subject(overrides: Partial<Subject> = {}): Subject {
  return {
    name: 'orders.created',
    format: 'json',
    owner: 'orders-team',
    lifecycle: 'ACTIVE',
    contentModel: 'OPEN',
    compatibilityPolicy: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
    latest: 1,
    versions: [version({ ordinal: 1, semanticVersion: '1.0.0' })],
    ...overrides,
  };
}

describe('SubjectCard', () => {
  let fixture: ComponentFixture<SubjectCard>;

  function render(value: Subject): HTMLElement {
    fixture.componentRef.setInput('subject', value);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [SubjectCard] });
    fixture = TestBed.createComponent(SubjectCard);
  });

  it('names the subject and the version latest resolves to', () => {
    const host = render(subject());

    expect(host.textContent).toContain('orders.created');
    expect(host.textContent).toContain('v1');
  });

  it('shows the gated version, not the newest one', () => {
    // ADR-017, restated here because this component reaches `latestVersion` on its own rather
    // than being handed the answer. Ordinal 2 is held at the gate; showing it would tell the
    // reader an unapproved contract is live.
    const host = render(
      subject({
        latest: 1,
        versions: [version({ ordinal: 1 }), version({ ordinal: 2, status: 'AWAITING_APPROVAL' })],
      }),
    );

    expect(host.textContent).toContain('v1');
    expect(host.textContent).not.toContain('v2');
  });

  it('shows an em dash and says so when no version is active yet', () => {
    const host = render(subject({ latest: null, versions: [] }));

    expect(host.textContent).toContain('—');
    expect(host.textContent).toContain('Never registered');
  });

  it('counts versions in words that survive being one', () => {
    // "1 versions" is the kind of thing nobody files a bug for and everybody notices.
    expect(render(subject()).textContent).toContain('1 version');
    expect(render(subject()).textContent).not.toContain('1 versions');

    const two = render(subject({ versions: [version({ ordinal: 1 }), version({ ordinal: 2 })] }));

    expect(two.textContent).toContain('2 versions');
  });

  it('counts the versions waiting for a decision, not the versions', () => {
    const host = render(
      subject({
        versions: [
          version({ ordinal: 1 }),
          version({ ordinal: 2, status: 'AWAITING_APPROVAL' }),
          version({ ordinal: 3, status: 'REJECTED' }),
          version({ ordinal: 4, status: 'AWAITING_APPROVAL' }),
        ],
      }),
    );

    expect(host.textContent).toContain('2 awaiting approval');
  });

  it('says nothing about approvals when nothing is waiting', () => {
    expect(render(subject()).textContent).not.toContain('awaiting approval');
  });

  it('badges a lifecycle only when it is not the ordinary one', () => {
    // Every card being labelled ACTIVE is noise that makes DEPRECATED harder to spot, which is
    // the one the badge exists for.
    expect(render(subject({ lifecycle: 'ACTIVE' })).textContent).not.toContain('ACTIVE');
    expect(render(subject({ lifecycle: 'DEPRECATED' })).textContent).toContain('DEPRECATED');
  });

  it('puts the exact timestamp in a title beside the relative one', () => {
    const host = render(subject());

    expect(host.querySelector('[title]')?.getAttribute('title')).toBe('2026-08-13T09:00:00.000Z');
  });

  it('offers no link while there is nowhere to go', () => {
    // `SubjectDetailPage` is M4.3. Until it exists, a card that looked clickable would be a
    // promise the app does not keep — and this assertion is what should fail, loudly, on the
    // day somebody adds the route and forgets to make the card use it.
    expect(render(subject()).querySelector('a')).toBeNull();
  });
});
