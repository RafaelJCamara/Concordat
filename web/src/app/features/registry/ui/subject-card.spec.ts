import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { NewestSubject } from '../../../domain/registry/environment-summary';
import { SubjectCard } from './subject-card';

// Presentational, so these are about what a reader can see. The card is handed what the
// registry's overview carries (ADR-037) rather than a subject with every version: `latest` is
// the ordinal the registry's gated pointer names (ADR-017), the counts are the registry's own,
// and an absent pointer is an em dash.

function subject(overrides: Partial<NewestSubject> = {}): NewestSubject {
  return {
    name: 'orders.created',
    format: 'json',
    owner: 'orders-team',
    lifecycle: 'ACTIVE',
    latest: 1,
    versions: 1,
    awaitingApproval: 0,
    createdAt: new Date('2026-08-13T09:00:00.000Z'),
    ...overrides,
  };
}

describe('SubjectCard', () => {
  let fixture: ComponentFixture<SubjectCard>;

  function render(value: NewestSubject): HTMLElement {
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

  it('shows the pointer it was given, not the number of versions', () => {
    // ADR-017: ordinals 2 and 3 are held at the gate, so four versions exist and the contract
    // that is live is v1. Deriving the label from the count would tell the reader an unapproved
    // schema is live.
    const host = render(subject({ latest: 1, versions: 3, awaitingApproval: 2 }));

    expect(host.textContent).toContain('v1');
    expect(host.textContent).not.toContain('v3');
  });

  it('shows an em dash when no version is active yet', () => {
    const host = render(subject({ latest: null, versions: 0 }));

    expect(host.textContent).toContain('—');
    expect(host.textContent).not.toContain('v0');
  });

  it('says when the subject was created, because that is what the overview orders by', () => {
    // A subject with no version still has a creation time, which is why a brand-new one is
    // never "never registered" or dropped to the bottom.
    const host = render(subject({ latest: null, versions: 0 }));

    expect(host.textContent).toContain('Created');
    expect(host.textContent).not.toContain('Registered');
  });

  it('counts versions in words that survive being one', () => {
    // "1 versions" is the kind of thing nobody files a bug for and everybody notices.
    expect(render(subject({ versions: 1 })).textContent).toContain('1 version');
    expect(render(subject({ versions: 1 })).textContent).not.toContain('1 versions');
    expect(render(subject({ versions: 2 })).textContent).toContain('2 versions');
  });

  it('says how many versions are waiting for a decision', () => {
    expect(render(subject({ versions: 4, awaitingApproval: 2 })).textContent).toContain(
      '2 awaiting approval',
    );
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

  it('carries no link of its own: the screen wraps it in one', () => {
    // The anchor is the screen's, so this layer stays router-free and the hover treatment is
    // driven from the wrapper.
    expect(render(subject()).querySelector('a')).toBeNull();
  });
});
