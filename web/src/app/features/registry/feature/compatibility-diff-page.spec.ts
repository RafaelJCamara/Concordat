import { signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { Divergence } from '../../../domain/registry/compatibility';
import type { VersionDiff } from '../../../domain/registry/diff';
import type { SchemaVersion } from '../../../domain/registry/subject';
import { DiffStore } from '../application/diff-store';
import { CompatibilityDiffPage } from './compatibility-diff-page';

// The screen's own decision is which of three answers to draw — the same document, a
// difference no rule reads, or a list of divergences — and that "no divergences" is never
// rendered as "nothing changed" while two different schema ids sit in the header.

function version(ordinal: number, status: SchemaVersion['status'] = 'ACTIVE'): SchemaVersion {
  return {
    ordinal,
    schemaId: `schema-${ordinal}`,
    semanticVersion: `1.${ordinal}.0`,
    status,
    changelog: null,
    registeredAt: new Date('2026-08-13T09:00:00Z'),
    registeredBy: 'ci',
    deprecated: false,
  };
}

const divergence: Divergence = {
  path: '/properties/discount',
  kind: 'required_field_added',
  direction: 'BACKWARD',
  surface: 'WIRE_JSON',
  message: 'A required field was added.',
  conflictsWithVersion: 1,
};

function diff(overrides: Partial<VersionDiff> = {}): VersionDiff {
  return {
    from: 1,
    to: 2,
    fromSchemaId: 'schema-1',
    toSchemaId: 'schema-2',
    identical: false,
    policy: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
    divergences: [divergence],
    ...overrides,
  };
}

function storeIn(state: {
  diff?: VersionDiff | null;
  loading?: boolean;
  error?: ConcordatError | null;
}) {
  return {
    diff: signal(state.diff ?? null),
    fromVersion: signal(version(1)),
    toVersion: signal(version(2, 'AWAITING_APPROVAL')),
    loading: signal(state.loading ?? false),
    error: signal(state.error ?? null),
    load: () => {},
  } as unknown as InstanceType<typeof DiffStore>;
}

function render(state: Parameters<typeof storeIn>[0]): {
  fixture: ComponentFixture<CompatibilityDiffPage>;
  host: HTMLElement;
} {
  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });

  TestBed.overrideComponent(CompatibilityDiffPage, {
    set: { providers: [{ provide: DiffStore, useValue: storeIn(state) }] },
  });

  const fixture = TestBed.createComponent(CompatibilityDiffPage);
  fixture.componentRef.setInput('name', 'orders.created');
  fixture.componentRef.setInput('from', '1');
  fixture.componentRef.setInput('to', '2');
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement };
}

describe('CompatibilityDiffPage', () => {
  it('links back to the subject', () => {
    const { host } = render({ diff: diff() });

    expect(host.querySelector('a[href="/subjects/orders.created"]')).not.toBeNull();
  });

  it('states which two versions are being compared, and which is the baseline', () => {
    const { host } = render({ diff: diff() });

    expect(host.querySelector('h1')?.textContent).toContain('v1');
    expect(host.querySelector('h1')?.textContent).toContain('v2');
    expect(host.textContent).toContain('Baseline');
    expect(host.textContent).toContain('Compared');
  });

  it('labels each end with its status, so a held proposal reads as one', () => {
    const { host } = render({ diff: diff() });

    expect(host.textContent).toContain('ACTIVE');
    expect(host.textContent).toContain('AWAITING_APPROVAL');
  });

  it('links each end to its own version screen', () => {
    const { host } = render({ diff: diff() });

    expect(host.querySelector('a[href="/subjects/orders.created/versions/1"]')).not.toBeNull();
    expect(host.querySelector('a[href="/subjects/orders.created/versions/2"]')).not.toBeNull();
  });

  it('offers the reverse comparison as a plain link', () => {
    const { host } = render({ diff: diff() });

    expect(
      host.querySelector('a[href="/subjects/orders.created/versions/2/diff/1"]')?.textContent,
    ).toContain('Swap direction');
  });

  it('names the policy the comparison is judged under', () => {
    const { host } = render({ diff: diff() });

    expect(host.textContent).toContain('BACKWARD · WIRE_JSON');
  });

  describe('the three answers', () => {
    it('lists the divergences when there are some', () => {
      const { host } = render({ diff: diff() });

      expect(host.textContent).toContain('/properties/discount');
      expect(host.textContent).toContain('A required field was added.');
    });

    it('says the documents are the same when the registry says they are', () => {
      const { host } = render({ diff: diff({ identical: true, divergences: [] }) });

      expect(host.textContent).toContain('These are the same document');
    });

    it('does not call two different documents "unchanged" because nothing was reported', () => {
      // Decisions-pending #12: under an open content model adding a property is invisible
      // to the engine. The header shows two different schema ids, and an "identical"
      // message under them would be the screen contradicting itself.
      const { host } = render({ diff: diff({ identical: false, divergences: [] }) });

      expect(host.textContent).toContain('The documents differ, and no compatibility rule');
      expect(host.textContent).not.toContain('These are the same document');
    });
  });

  it('shows a skeleton and no verdict while loading', () => {
    const { host } = render({ loading: true });

    expect(host.querySelector('[aria-busy="true"]')).not.toBeNull();
    expect(host.querySelector('h1')).toBeNull();
  });

  it('renders the registry refusal when there is no diff', () => {
    const { host } = render({
      error: new ConcordatError({
        status: 404,
        code: 'version_not_found',
        detail: 'Subject has no version 9.',
      }),
    });

    expect(host.textContent).toContain('Could not compare these versions');
    expect(host.textContent).toContain('Subject has no version 9.');
  });
});
