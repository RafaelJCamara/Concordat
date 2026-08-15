import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';
import type { SchemaVersion, Subject } from '../../../domain/registry/subject';
import { SubjectTable } from './subject-table';

// Presentational, so the tests are about what a reader can see and nothing else. The three
// cells worth checking are the ones that encode a decision rather than a field: `latest`
// follows the approval gate, an absent version is an em dash, and the pending count is a
// count of held versions rather than of versions.

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

describe('SubjectTable', () => {
  let fixture: ComponentFixture<SubjectTable>;

  function render(subjects: readonly Subject[]): HTMLElement {
    fixture.componentRef.setInput('subjects', subjects);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(() => {
    // The router is here for `routerLink` on the subject name, which is the one piece of
    // routing this presentational component owns — see the note on the component for why a
    // table cell cannot take its anchor from the screen above it.
    TestBed.configureTestingModule({ imports: [SubjectTable], providers: [provideRouter([])] });
    fixture = TestBed.createComponent(SubjectTable);
  });

  it('renders one row per subject', () => {
    const host = render([subject({ name: 'orders.created' }), subject({ name: 'orders.shipped' })]);

    expect(host.querySelectorAll('tbody tr')).toHaveLength(2);
  });

  it('renders nothing but the header when there are no subjects', () => {
    // The empty state is the page's business, not the table's. A table that drew its own
    // "no subjects" row would compete with it.
    const host = render([]);

    expect(host.querySelectorAll('tbody tr')).toHaveLength(0);
    expect(host.querySelectorAll('thead tr')).toHaveLength(1);
  });

  it('shows the version the latest pointer resolves to, not the newest one', () => {
    // ADR-017 as the user sees it. Ordinal 2 exists and is held at the gate; showing it as
    // `latest` would tell every reader that an unapproved contract is live.
    const host = render([
      subject({
        latest: 1,
        versions: [
          version({ ordinal: 1, semanticVersion: '1.0.0' }),
          version({ ordinal: 2, semanticVersion: '2.0.0', status: 'AWAITING_APPROVAL' }),
        ],
      }),
    ]);

    expect(host.textContent).toContain('v1');
    expect(host.textContent).not.toContain('v2');
  });

  it('shows an em dash when no version is active yet', () => {
    // Not "0" and not "none". A subject with no active version is the ordinary state right
    // after creation, and a zero reads like a count that went wrong.
    const host = render([subject({ latest: null, versions: [] })]);

    expect(host.textContent).toContain('—');
  });

  it('counts the versions waiting for a decision', () => {
    const host = render([
      subject({
        latest: 1,
        versions: [
          version({ ordinal: 1 }),
          version({ ordinal: 2, status: 'AWAITING_APPROVAL' }),
          version({ ordinal: 3, status: 'REJECTED' }),
          version({ ordinal: 4, status: 'AWAITING_APPROVAL' }),
        ],
      }),
    ]);

    expect(host.textContent).toContain('2 awaiting approval');
  });

  it('says nothing about approvals when nothing is waiting', () => {
    expect(render([subject()]).textContent).not.toContain('awaiting approval');
  });

  it('badges a lifecycle only when it is not the ordinary one', () => {
    // Every subject being labelled `ACTIVE` is noise that makes `DEPRECATED` harder to spot,
    // which is the one the badge exists for.
    expect(render([subject({ lifecycle: 'ACTIVE' })]).textContent).not.toContain('ACTIVE');
    expect(render([subject({ lifecycle: 'DEPRECATED' })]).textContent).toContain('DEPRECATED');
  });

  it('puts the exact timestamp in a title beside the relative one', () => {
    // "2 months ago" is the right thing to scan a list with and the wrong thing to
    // reconcile an incident against. The next question after "recently?" is always "when
    // exactly?", and it has to be answerable without leaving the row.
    const host = render([subject()]);
    const cell = host.querySelector('td[title]');

    expect(cell?.getAttribute('title')).toBe('2026-08-13T09:00:00.000Z');
  });

  it('captions the table so the latest column is not a guess', () => {
    expect(render([subject()]).querySelector('caption')?.textContent).toContain('latest');
  });
});
