import { signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { SessionStore } from '../../../core/auth/session-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { Scope } from '../../../domain/identity/scope';
import type { SchemaVersion, Subject } from '../../../domain/registry/subject';
import { SubjectDetailStore } from '../application/subject-detail-store';
import { SubjectDetailPage } from './subject-detail-page';

// The screen's own logic is which of four states it draws, and how it words two of them.
// Loading, failing and ordering belong to `SubjectDetailStore` and are tested there.

function version(ordinal: number): SchemaVersion {
  return {
    ordinal,
    schemaId: 'abcdef0123456789abcdef0123456789',
    semanticVersion: null,
    status: 'ACTIVE',
    changelog: null,
    registeredAt: new Date('2026-08-13T09:00:00Z'),
    registeredBy: 'ci',
    deprecated: false,
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
    latest: null,
    versions: [],
    ...overrides,
  };
}

/**
 * A `SubjectDetailStore` in a given state, stubbed.
 *
 * The store is replaced rather than the API underneath it: the boundaries rule says a routed
 * component may not reach into `data-access`, and a test that imported `SubjectsApi` to build
 * a fake would have done from the same folder the thing the rule exists to prevent.
 */
function storeIn(state: {
  subject?: Subject | null;
  loading?: boolean;
  error?: ConcordatError | null;
}) {
  const value = state.subject ?? null;

  return {
    subject: signal(value),
    loading: signal(state.loading ?? false),
    error: signal(state.error ?? null),
    latest: signal(value?.versions.find((v) => v.ordinal === value.latest) ?? null),
    pending: signal(value?.versions.filter((v) => v.status === 'AWAITING_APPROVAL') ?? []),
    versions: signal([...(value?.versions ?? [])].sort((a, b) => b.ordinal - a.ordinal)),
    load: () => {},
  } as unknown as InstanceType<typeof SubjectDetailStore>;
}

function render(
  state: Parameters<typeof storeIn>[0],
  scopes: readonly Scope[] = [],
): {
  fixture: ComponentFixture<SubjectDetailPage>;
  host: HTMLElement;
} {
  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });

  // `overrideComponent` has to come before the first `inject`, which instantiates the module
  // and freezes the overrides.
  TestBed.overrideComponent(SubjectDetailPage, {
    set: { providers: [{ provide: SubjectDetailStore, useValue: storeIn(state) }] },
  });

  // Signed in before the component renders, because `*cdIfScope` decides on first render and
  // this page's write affordance is what several of these tests are about.
  TestBed.inject(SessionStore).signIn({ credential: 't', actor: 'someone', scopes: [...scopes] });

  const fixture = TestBed.createComponent(SubjectDetailPage);
  fixture.componentRef.setInput('name', state.subject?.name ?? 'orders.created');
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement };
}

describe('SubjectDetailPage', () => {
  it('offers a way back to the list that works on a first load', () => {
    // A real link rather than `history.back()`: someone who arrived from a pasted URL has no
    // history, and a control that does nothing on first load is worse than one that always
    // goes to the list.
    const { host } = render({ subject: subject() });

    expect(host.querySelector('a[href="/subjects"]')).not.toBeNull();
  });

  it('says the subject exists but has no contract yet', () => {
    // The ordinary state right after creating one, and the wording has to say so — a bare
    // "nothing here" reads as though the create silently failed.
    const { host } = render({ subject: subject({ versions: [] }) });

    expect(host.textContent).toContain('No versions yet');
    expect(host.querySelector('cd-version-table')).toBeNull();
  });

  it('draws the version table once there is a version', () => {
    const { host } = render({ subject: subject({ versions: [version(1)], latest: 1 }) });

    expect(host.querySelector('cd-version-table')).not.toBeNull();
    expect(host.textContent).not.toContain('No versions yet');
  });

  it('says "Inherited" rather than an em dash when no policy is set on the subject', () => {
    // Nulls together mean the subject inherits the environment default, which is not the
    // same as having no policy. An em dash would read as "nothing is checked".
    const { host } = render({
      subject: subject({ compatibilityPolicy: { mode: null, surface: null } }),
    });

    expect(host.textContent).toContain('Inherited');
  });

  it('names the policy when the subject sets its own', () => {
    const { host } = render({
      subject: subject({ compatibilityPolicy: { mode: 'FULL', surface: 'WIRE_JSON' } }),
    });

    expect(host.textContent).toContain('FULL');
  });

  it('shows the latest ordinal as an em dash when nothing is active', () => {
    const { host } = render({ subject: subject({ versions: [], latest: null }) });

    expect(host.textContent).toContain('—');
  });

  it('offers a retry when the load failed', () => {
    const { host } = render({
      error: new ConcordatError({
        status: 404,
        code: 'subject_not_found',
        detail: 'No such thing.',
      }),
    });

    expect(host.textContent).toContain('No such thing.');
    expect(host.querySelector('button')?.textContent).toContain('Try again');
  });

  it('draws a skeleton rather than an empty state while loading', () => {
    // The flicker this prevents: "No versions yet" rendered for as long as the request takes,
    // and then replaced by rows.
    const { host } = render({ loading: true });

    expect(host.textContent).not.toContain('No versions yet');
    expect(host.querySelector('[aria-busy="true"]')).not.toBeNull();
  });

  describe('the write affordance', () => {
    it('is absent for a reader, not disabled', () => {
      // ADR-018. A disabled button invites a support ticket asking to be given the
      // permission; an absent one does not raise the question.
      const { host } = render({ subject: subject({ versions: [version(1)], latest: 1 }) }, [
        'subject:read',
      ]);

      expect(host.textContent).not.toContain('New version');
    });

    it('is there for someone who may register', () => {
      const { host } = render({ subject: subject({ versions: [version(1)], latest: 1 }) }, [
        'subject:admin',
      ]);

      expect(host.textContent).toContain('New version');
    });

    it('points at the write route, which the guard also protects', () => {
      const { host } = render({ subject: subject({ versions: [version(1)], latest: 1 }) }, [
        'subject:write',
      ]);

      expect(host.querySelector('a[href="/subjects/orders.created/versions/new"]')).not.toBeNull();
    });
  });
});
