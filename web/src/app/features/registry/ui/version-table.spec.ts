import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';
import type { SchemaVersion } from '../../../domain/registry/subject';
import { VersionTable } from './version-table';

// Presentational, so these are about what a reader can see. Two decisions are encoded rather
// than displayed and are what this file is really for: the `latest` badge comes from the
// server's pointer and is never derived here, and each status keeps its own text so the tone
// is not the only thing distinguishing it.

function version(overrides: Partial<SchemaVersion> & { ordinal: number }): SchemaVersion {
  return {
    schemaId: 'abcdef0123456789abcdef0123456789',
    semanticVersion: null,
    status: 'ACTIVE',
    changelog: null,
    registeredAt: new Date('2026-08-13T09:00:00Z'),
    registeredBy: 'ci',
    deprecated: false,
    ...overrides,
  };
}

describe('VersionTable', () => {
  let fixture: ComponentFixture<VersionTable>;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [VersionTable], providers: [provideRouter([])] });
    fixture = TestBed.createComponent(VersionTable);
  });

  function render(versions: readonly SchemaVersion[], latest: number | null = null): HTMLElement {
    fixture.componentRef.setInput('subject', 'orders.created');
    fixture.componentRef.setInput('versions', versions);
    fixture.componentRef.setInput('latest', latest);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  it('renders one row per version', () => {
    const host = render([version({ ordinal: 2 }), version({ ordinal: 1 })]);

    expect(host.querySelectorAll('tbody tr')).toHaveLength(2);
  });

  it('links each version to its own route', () => {
    // The URL people paste. A query parameter could not express it without carrying whatever
    // else the page happened to have.
    const host = render([version({ ordinal: 2 })]);

    expect(host.querySelector('tbody a')?.getAttribute('href')).toBe(
      '/subjects/orders.created/versions/2',
    );
  });

  it('encodes a subject name that would otherwise change the route', () => {
    fixture.componentRef.setInput('subject', 'orders/created');
    fixture.componentRef.setInput('versions', [version({ ordinal: 1 })]);
    fixture.componentRef.setInput('latest', null);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('tbody a')?.getAttribute('href')).toBe(
      '/subjects/orders%2Fcreated/versions/1',
    );
  });

  it('badges the version the pointer resolves to, not the highest ordinal', () => {
    // v3 is awaiting approval and deliberately not current. Deriving `latest` here as
    // `max(ordinal)` would badge the unapproved one as the live contract.
    const host = render(
      [version({ ordinal: 3, status: 'AWAITING_APPROVAL' }), version({ ordinal: 2 })],
      2,
    );

    const rows = host.querySelectorAll('tbody tr');

    expect(rows[0]?.textContent).not.toContain('latest');
    expect(rows[1]?.textContent).toContain('latest');
  });

  it('badges nothing as latest when the subject has no active version', () => {
    // Scoped to the rows: the caption explains what `latest` means and says the word.
    const host = render([version({ ordinal: 1, status: 'AWAITING_APPROVAL' })], null);

    expect(host.querySelector('tbody')?.textContent).not.toContain('latest');
  });

  it('names every status in text, not only by tone', () => {
    // The tint is never the only signal — it has to survive greyscale and colour blindness.
    const host = render([
      version({ ordinal: 4, status: 'DISMISSED' }),
      version({ ordinal: 3, status: 'REJECTED' }),
      version({ ordinal: 2, status: 'AWAITING_APPROVAL' }),
      version({ ordinal: 1, status: 'ACTIVE' }),
    ]);

    expect(host.textContent).toContain('DISMISSED');
    expect(host.textContent).toContain('REJECTED');
    expect(host.textContent).toContain('AWAITING_APPROVAL');
    expect(host.textContent).toContain('ACTIVE');
  });

  it('shows an em dash when a version carries no semantic version', () => {
    // The label is optional and unverified. An empty cell reads as a rendering fault.
    const host = render([version({ ordinal: 1, semanticVersion: null })]);

    expect(host.querySelectorAll('tbody td')[1]?.textContent?.trim()).toBe('—');
  });

  it('truncates the schema id but keeps the whole one in a title', () => {
    // 32 characters of hex would dominate the row; the prefix is what gets compared against
    // a log line, and the title is what gets copied.
    const host = render([version({ ordinal: 1 })]);
    const cell = host.querySelectorAll('tbody td')[2];

    expect(cell?.textContent?.trim()).toBe('abcdef012345…');
    expect(cell?.getAttribute('title')).toBe('abcdef0123456789abcdef0123456789');
  });

  it('marks a deprecated version alongside its status', () => {
    // Deprecation is orthogonal to the approval gate: an active version can be deprecated,
    // and showing only one of the two would hide the fact people need.
    const host = render([version({ ordinal: 1, deprecated: true })]);

    expect(host.textContent).toContain('ACTIVE');
    expect(host.textContent).toContain('DEPRECATED');
  });

  it('puts the exact timestamp in a title beside the relative one', () => {
    const host = render([version({ ordinal: 1 })]);
    const cell = host.querySelectorAll('tbody td')[3];

    expect(cell?.getAttribute('title')).toBe('2026-08-13T09:00:00.000Z');
  });
});
