import { signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import type { SchemaVersion, Subject } from '../../../domain/registry/subject';
import { SubjectListStore } from '../application/subject-list-store';
import { DashboardPage } from './dashboard-page';

// Every number on this screen is derived from the subject list rather than fetched, so the
// arithmetic is the component's own and is worth pinning. The ordering test is the one that
// earns its keep: "most recent" has to mean the registration time of the version `latest`
// points at, and a subject with no active version has no such time at all.

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

function subject(overrides: Partial<Subject> & { name: string }): Subject {
  return {
    format: 'json',
    owner: 'orders-team',
    lifecycle: 'ACTIVE',
    contentModel: 'OPEN',
    compatibilityPolicy: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
    latest: 1,
    versions: [version({ ordinal: 1 })],
    ...overrides,
  };
}

/** The stat card with this title, as the text a reader would see on it. */
function card(host: HTMLElement, title: string): string {
  const match = [...host.querySelectorAll('cd-stat-card')].find((element) =>
    element.textContent?.includes(title),
  );

  return match?.textContent ?? '';
}

/**
 * A loaded `SubjectListStore`, stubbed.
 *
 * The store is replaced rather than the API underneath it, because the boundaries rule says a
 * routed component may not reach into `data-access` — and a test that imports `SubjectsApi` to
 * build a fake has just done, from the same folder, the thing the rule exists to prevent. The
 * store is this page's only collaborator, so it is also the only thing worth substituting.
 */
function loadedStore(subjects: readonly Subject[]) {
  return {
    subjects: signal(subjects),
    loading: signal(false),
    error: signal(null),
    isEmpty: signal(subjects.length === 0),
    load: () => {},
  } as unknown as InstanceType<typeof SubjectListStore>;
}

function render(subjects: readonly Subject[]): {
  fixture: ComponentFixture<DashboardPage>;
  host: HTMLElement;
} {
  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });

  // The page provides its own store, so a root-level provider would never be reached. This
  // replaces the component-level one, which is the only place it is registered.
  TestBed.overrideComponent(DashboardPage, {
    set: { providers: [{ provide: SubjectListStore, useValue: loadedStore(subjects) }] },
  });

  const fixture = TestBed.createComponent(DashboardPage);
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement };
}

describe('DashboardPage', () => {
  it('counts subjects, and versions across all of them', () => {
    const { host } = render([
      subject({ name: 'a', versions: [version({ ordinal: 1 }), version({ ordinal: 2 })] }),
      subject({ name: 'b', versions: [version({ ordinal: 1 })] }),
    ]);

    expect(card(host, 'Subjects')).toContain('2');
    expect(card(host, 'Versions')).toContain('3');
  });

  it('counts versions held at the approval gate across all subjects', () => {
    const { host } = render([
      subject({
        name: 'a',
        versions: [version({ ordinal: 1 }), version({ ordinal: 2, status: 'AWAITING_APPROVAL' })],
      }),
      subject({
        name: 'b',
        versions: [version({ ordinal: 1, status: 'AWAITING_APPROVAL' })],
      }),
      subject({ name: 'c', versions: [version({ ordinal: 1, status: 'REJECTED' })] }),
    ]);

    // Two awaiting, one rejected. A rejected version is a decision that has been made, and
    // counting it here would keep a resolved thing on somebody's to-do list forever.
    expect(card(host, 'Awaiting approval')).toContain('2');
  });

  it('says nothing is waiting rather than leaving a bare zero', () => {
    // A zero on an approvals counter reads equally as "nothing needs approving" and "approvals
    // are not switched on". The other two counters are not ambiguous that way.
    const { host } = render([subject({ name: 'a' })]);

    expect(card(host, 'Awaiting approval')).toContain('Nothing waiting');
  });

  it('orders recent subjects by when their active version was registered', () => {
    const { host } = render([
      subject({
        name: 'oldest',
        versions: [version({ ordinal: 1, registeredAt: new Date('2026-01-01T00:00:00Z') })],
      }),
      subject({
        name: 'newest',
        versions: [version({ ordinal: 1, registeredAt: new Date('2026-08-01T00:00:00Z') })],
      }),
      subject({
        name: 'middle',
        versions: [version({ ordinal: 1, registeredAt: new Date('2026-04-01T00:00:00Z') })],
      }),
    ]);

    const names = [...host.querySelectorAll('cd-subject-card h3')].map((h) =>
      h.textContent?.trim(),
    );

    expect(names).toEqual(['newest', 'middle', 'oldest']);
  });

  it('still shows a subject that has no active version, at the end', () => {
    // It is the newest thing in the registry by one reading, and dropping it is how somebody
    // creates a subject and concludes the registry did not save it. Last, because there is no
    // registration time to sort it by, not because it matters least.
    const { host } = render([
      subject({ name: 'has-one' }),
      subject({ name: 'brand-new', latest: null, versions: [] }),
    ]);

    const names = [...host.querySelectorAll('cd-subject-card h3')].map((h) =>
      h.textContent?.trim(),
    );

    expect(names).toEqual(['has-one', 'brand-new']);
  });

  it('shows at most six, and points at the full list', () => {
    const { host } = render(
      Array.from({ length: 9 }, (_, index) => subject({ name: `subject-${index}` })),
    );

    expect(host.querySelectorAll('cd-subject-card')).toHaveLength(6);
    expect(host.textContent).toContain('View all');
  });

  it('explains an empty registry instead of showing three zeroes and nothing else', () => {
    const { host } = render([]);

    expect(host.textContent).toContain('No subjects yet');
  });
});
