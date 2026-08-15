import { signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { SchemaDocument } from '../../../domain/registry/schema';
import type { SchemaVersion } from '../../../domain/registry/subject';
import { VersionDetailStore } from '../application/version-detail-store';
import { VersionDetailPage } from './version-detail-page';

// The screen's own decision is what to draw when only half of it loaded. The store keeps the
// metadata and drops the document, and this file is what proves the template actually uses
// that rather than blanking on any error.

function version(overrides: Partial<SchemaVersion> = {}): SchemaVersion {
  return {
    ordinal: 2,
    schemaId: 'abcdef0123456789abcdef0123456789',
    semanticVersion: '1.2.0',
    status: 'ACTIVE',
    changelog: null,
    registeredAt: new Date('2026-08-13T09:00:00Z'),
    registeredBy: 'alice@example.com',
    deprecated: false,
    ...overrides,
  };
}

const document: SchemaDocument = {
  schemaId: 'abcdef0123456789abcdef0123456789',
  format: 'json',
  text: '{"type":"object"}',
  references: [],
};

function storeIn(state: {
  version?: SchemaVersion | null;
  document?: SchemaDocument | null;
  loading?: boolean;
  loadingDocument?: boolean;
  error?: ConcordatError | null;
  isLatest?: boolean;
  previous?: SchemaVersion | null;
}) {
  return {
    subject: signal(null),
    version: signal(state.version ?? null),
    document: signal(state.document ?? null),
    loading: signal(state.loading ?? false),
    loadingDocument: signal(state.loadingDocument ?? false),
    error: signal(state.error ?? null),
    isLatest: signal(state.isLatest ?? false),
    previous: signal(state.previous ?? null),
    load: () => {},
  } as unknown as InstanceType<typeof VersionDetailStore>;
}

function render(state: Parameters<typeof storeIn>[0]): {
  fixture: ComponentFixture<VersionDetailPage>;
  host: HTMLElement;
} {
  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });

  TestBed.overrideComponent(VersionDetailPage, {
    set: { providers: [{ provide: VersionDetailStore, useValue: storeIn(state) }] },
  });

  const fixture = TestBed.createComponent(VersionDetailPage);
  fixture.componentRef.setInput('name', 'orders.created');
  fixture.componentRef.setInput('ordinal', '2');
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement };
}

describe('VersionDetailPage', () => {
  it('links back to the subject it belongs to', () => {
    const { host } = render({ version: version() });

    expect(host.querySelector('a[href="/subjects/orders.created"]')).not.toBeNull();
  });

  it('names who registered it and when', () => {
    const { host } = render({ version: version() });

    expect(host.textContent).toContain('alice@example.com');
    expect(host.querySelector('[title="2026-08-13T09:00:00.000Z"]')).not.toBeNull();
  });

  it('keeps the metadata on screen when only the document failed', () => {
    // The partial-failure case. Blanking the page would throw away everything the first
    // request proved, which is most of what a reader came for.
    const { host } = render({
      version: version(),
      error: new ConcordatError({ status: 0, code: 'registry_unreachable', detail: 'Offline.' }),
    });

    expect(host.textContent).toContain('alice@example.com');
    expect(host.textContent).toContain('Could not load the schema document');
    expect(host.textContent).toContain('Offline.');
  });

  it('reports a missing version as the whole screen failing', () => {
    // Nothing resolved, so there is no metadata to keep — a different message from the one
    // above, and deliberately so.
    const { host } = render({
      version: null,
      error: new ConcordatError({
        status: 404,
        code: 'version_not_found',
        detail: "No version '9' on subject 'orders.created'.",
      }),
    });

    expect(host.textContent).toContain('Could not load this version');
    expect(host.textContent).not.toContain('Could not load the schema document');
  });

  it('draws a skeleton for the document while the metadata is already up', () => {
    // Why the store carries two loading flags: the header is known and worth reading before
    // the second round trip finishes.
    const { host } = render({ version: version(), loadingDocument: true });

    expect(host.textContent).toContain('alice@example.com');
    expect(host.querySelector('cd-schema-view')).toBeNull();
    expect(host.querySelector('[aria-busy="true"]')).not.toBeNull();
  });

  it('draws the document once it arrives', () => {
    const { host } = render({ version: version(), document });

    expect(host.querySelector('cd-schema-view')).not.toBeNull();
  });

  it('badges the version as latest when the pointer resolves to it', () => {
    const { host } = render({ version: version(), isLatest: true });

    expect(host.textContent).toContain('latest');
  });

  it('does not badge a version the pointer has not released', () => {
    // A higher ordinal awaiting approval is deliberately not current, and labelling it
    // `latest` would present an unapproved schema as the live contract.
    const { host } = render({
      version: version({ ordinal: 3, status: 'AWAITING_APPROVAL' }),
      isLatest: false,
    });

    expect(host.textContent).not.toContain('latest');
  });

  it('names the status in text rather than by colour alone', () => {
    const { host } = render({ version: version({ status: 'AWAITING_APPROVAL' }) });

    expect(host.textContent).toContain('AWAITING_APPROVAL');
  });

  it('shows a changelog when the registration carried one', () => {
    const { host } = render({ version: version({ changelog: 'Added the discount field.' }) });

    expect(host.textContent).toContain('Added the discount field.');
  });

  it('says nothing about a changelog when there is none', () => {
    // An empty "Changelog" heading is a question the reader then has to answer.
    const { host } = render({ version: version({ changelog: null }) });

    expect(host.textContent).not.toContain('Changelog');
  });

  it('does not offer a comparison yet, because there is no diff route', () => {
    // Guards against re-adding the link before `CompatibilityDiffPage` exists: it would hit
    // the wildcard and redirect to the dashboard.
    const { host } = render({ version: version(), previous: version({ ordinal: 1 }) });

    expect(host.textContent).not.toContain('Compare with');
  });
});
