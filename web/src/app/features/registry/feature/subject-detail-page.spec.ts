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
import { SubjectSettingsStore, type SettingsOutcome } from '../application/subject-settings-store';
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
  refreshError?: ConcordatError | null;
}) {
  const value = state.subject ?? null;

  return {
    subject: signal(value),
    loading: signal(state.loading ?? false),
    error: signal(state.error ?? null),
    refreshError: signal(state.refreshError ?? null),
    latest: signal(value?.versions.find((v) => v.ordinal === value.latest) ?? null),
    pending: signal(value?.versions.filter((v) => v.status === 'AWAITING_APPROVAL') ?? []),
    versions: signal([...(value?.versions ?? [])].sort((a, b) => b.ordinal - a.ordinal)),
    load: () => {},
  } as unknown as InstanceType<typeof SubjectDetailStore>;
}

/**
 * A `SubjectSettingsStore` that records what the page asks of it, and holds whatever state a test
 * wants shown. The writes themselves are the store's own spec's business.
 */
function settingsStub(
  state: { outcome?: SettingsOutcome | null; ownerSaving?: boolean; policySaving?: boolean } = {},
) {
  const owners: { subject: string; owner: string }[] = [];
  const policies: { subject: string; policy: unknown }[] = [];

  return {
    owners,
    policies,
    outcome: signal(state.outcome ?? null),
    ownerSaving: signal(state.ownerSaving ?? false),
    ownerError: signal(null),
    policySaving: signal(state.policySaving ?? false),
    policyError: signal(null),
    changeOwner: (request: { subject: string; owner: string }) => owners.push(request),
    setPolicy: (request: { subject: string; policy: unknown }) => policies.push(request),
    reset: () => {},
  };
}

function render(
  state: Parameters<typeof storeIn>[0],
  scopes: readonly Scope[] = [],
  settings: ReturnType<typeof settingsStub> = settingsStub(),
): {
  fixture: ComponentFixture<SubjectDetailPage>;
  host: HTMLElement;
  settings: ReturnType<typeof settingsStub>;
} {
  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });

  // `overrideComponent` has to come before the first `inject`, which instantiates the module
  // and freezes the overrides.
  TestBed.overrideComponent(SubjectDetailPage, {
    set: {
      providers: [
        { provide: SubjectDetailStore, useValue: storeIn(state) },
        { provide: SubjectSettingsStore, useValue: settings },
      ],
    },
  });

  // Signed in before the component renders, because `*cdIfScope` decides on first render and
  // this page's write affordance is what several of these tests are about.
  TestBed.inject(SessionStore).signIn({ credential: 't', actor: 'someone', scopes: [...scopes] });

  const fixture = TestBed.createComponent(SubjectDetailPage);
  fixture.componentRef.setInput('name', state.subject?.name ?? 'orders.created');
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, settings };
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

  describe('what a subject shows of its compatibility policy', () => {
    it('names the surface as well as the mode, in separate elements', () => {
      const { host } = render({
        subject: subject({ compatibilityPolicy: { mode: 'FULL', surface: 'SOURCE' } }),
      });
      const cells = [...host.querySelectorAll('dd span')].map((s) => s.textContent?.trim());

      // Two spans, so a text match on the mode alone still finds it.
      expect(cells).toContain('FULL');
      expect(cells).toContain('· SOURCE');
    });
  });

  describe('changing the owner and the policy', () => {
    const buttons = (host: HTMLElement) =>
      [...host.querySelectorAll('button')].map((b) => b.textContent?.trim());

    it('offers neither to a reader, and renders no disabled control in their place', () => {
      // ADR-018: absent, not disabled. A disabled "Change owner" invites a request for access.
      const { host } = render({ subject: subject() }, ['subject:read']);

      expect(buttons(host)).not.toContain('Change owner');
      expect(buttons(host)).not.toContain('Change policy');
      expect(host.textContent).not.toContain('Change owner');
      expect(host.textContent).not.toContain('Change policy');
    });

    it('offers the owner to a writer and not the policy, which the registry holds to admin', () => {
      // Probed with a real `subject:write` key: PATCH 200, PUT 403.
      const { host } = render({ subject: subject() }, ['subject:write']);

      expect(buttons(host)).toContain('Change owner');
      expect(buttons(host)).not.toContain('Change policy');
    });

    it('offers both to an admin, whose scope implies the writer’s', () => {
      const { host } = render({ subject: subject() }, ['subject:admin']);

      expect(buttons(host)).toContain('Change owner');
      expect(buttons(host)).toContain('Change policy');
    });

    it('offers neither for a retired subject, which the registry refuses with 409', () => {
      // Retired is terminal, so there is no snapshot that could be wrong about it.
      const { host } = render({ subject: subject({ lifecycle: 'RETIRED' }) }, ['subject:admin']);

      expect(buttons(host)).not.toContain('Change owner');
      expect(buttons(host)).not.toContain('Change policy');
    });

    it('still offers both for a deprecated subject, which accepts both', () => {
      const { host } = render({ subject: subject({ lifecycle: 'DEPRECATED' }) }, ['subject:admin']);

      expect(buttons(host)).toContain('Change owner');
      expect(buttons(host)).toContain('Change policy');
    });

    it('opens one editor at a time, and closing it again is the same button', () => {
      const { fixture, host } = render({ subject: subject() }, ['subject:admin']);
      const click = (label: string) => {
        [...host.querySelectorAll('button')].find((b) => b.textContent?.trim() === label)!.click();
        fixture.detectChanges();
      };

      expect(host.querySelector('cd-subject-owner-editor')).toBeNull();

      click('Change owner');
      expect(host.querySelector('cd-subject-owner-editor')).not.toBeNull();
      expect(host.querySelector('cd-subject-policy-editor')).toBeNull();

      click('Change policy');
      expect(host.querySelector('cd-subject-owner-editor')).toBeNull();
      expect(host.querySelector('cd-subject-policy-editor')).not.toBeNull();

      click('Change policy');
      expect(host.querySelector('cd-subject-policy-editor')).toBeNull();
    });

    it('hands what was confirmed to the store, with the subject it is for', () => {
      const { fixture, host, settings } = render({ subject: subject() }, ['subject:admin']);

      [...host.querySelectorAll('button')]
        .find((b) => b.textContent?.trim() === 'Change owner')!
        .click();
      fixture.detectChanges();

      const editor = fixture.debugElement.query((d) => d.name === 'cd-subject-owner-editor');
      (editor.componentInstance as { confirmed: { emit(v: string): void } }).confirmed.emit('bob');

      expect(settings.owners).toEqual([{ subject: 'orders.created', owner: 'bob' }]);
    });

    it('says what the registry answered, in a status line, once a change has landed', () => {
      // The editor that was open has closed and focus has gone with it, so the outcome is a live
      // region and the words are the registry's: the owner it stored, not the one typed.
      const { host } = render(
        { subject: subject() },
        ['subject:admin'],
        settingsStub({ outcome: { kind: 'owner', subject: 'orders.created', owner: 'bob' } }),
      );
      const status = host.querySelector('[role="status"]');

      expect(status?.textContent).toContain('The owner of orders.created is now bob');
      expect(host.querySelector('cd-subject-owner-editor')).toBeNull();
    });

    it('says an inheriting answer as inheriting, not as a policy', () => {
      const { host } = render(
        { subject: subject() },
        ['subject:admin'],
        settingsStub({
          outcome: {
            kind: 'policy',
            subject: 'orders.created',
            policy: { mode: null, surface: null },
          },
        }),
      );

      expect(host.querySelector('[role="status"]')?.textContent).toContain(
        'now has no policy of its own',
      );
    });

    it('says a policy the way the registry answered it', () => {
      const { host } = render(
        { subject: subject() },
        ['subject:admin'],
        settingsStub({
          outcome: {
            kind: 'policy',
            subject: 'orders.created',
            policy: { mode: 'FULL_TRANSITIVE', surface: 'WIRE_JSON' },
          },
        }),
      );

      expect(host.querySelector('[role="status"]')?.textContent).toContain(
        'FULL_TRANSITIVE · WIRE_JSON',
      );
    });

    it('says the change is in when the page cannot read it back, apart from a failed load', () => {
      const { host } = render({
        subject: subject(),
        refreshError: new ConcordatError({
          status: 0,
          code: 'network_error',
          detail: 'The registry did not answer.',
        }),
      });

      expect(host.textContent).toContain('The change is in, but this page could not read it back');
      expect(host.textContent).toContain('The registry did not answer.');
      expect(host.textContent).not.toContain('Could not load');
    });
  });
});
