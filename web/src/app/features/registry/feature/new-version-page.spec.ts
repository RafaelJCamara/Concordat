import { signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { RegistrationOutcome } from '../../../domain/registry/registration';
import { NewVersionStore } from '../application/new-version-store';
import { NewVersionPage } from './new-version-page';

// The screen's job is to say which of three things happened, and the three are genuinely
// different: registered, accepted-but-held, and already-the-tip. Conflating the middle one
// with a failure would have people re-submitting a change that already landed; conflating the
// third with the first would have them believe they created a version they did not.

function outcome(overrides: Partial<RegistrationOutcome> = {}): RegistrationOutcome {
  return {
    subject: 'orders.created',
    ordinal: 2,
    schemaId: 'schema-2',
    status: 'ACTIVE',
    created: true,
    divergences: [],
    portability: [],
    ...overrides,
  };
}

function storeIn(state: {
  outcome?: RegistrationOutcome | null;
  submitting?: boolean;
  error?: ConcordatError | null;
  nextOrdinal?: number | null;
}) {
  const value = state.outcome ?? null;

  return {
    subject: signal(null),
    loading: signal(false),
    submitting: signal(state.submitting ?? false),
    outcome: signal(value),
    error: signal(state.error ?? null),
    nextOrdinal: signal(state.nextOrdinal ?? 2),
    held: signal(value?.status === 'AWAITING_APPROVAL'),
    loadSubject: () => {},
    submit: () => {},
    reset: () => {},
  } as unknown as InstanceType<typeof NewVersionStore>;
}

function render(state: Parameters<typeof storeIn>[0] = {}): {
  fixture: ComponentFixture<NewVersionPage>;
  host: HTMLElement;
} {
  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });

  TestBed.overrideComponent(NewVersionPage, {
    set: { providers: [{ provide: NewVersionStore, useValue: storeIn(state) }] },
  });

  const fixture = TestBed.createComponent(NewVersionPage);
  fixture.componentRef.setInput('name', 'orders.created');
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement };
}

/** Types into a control and lets the form react, as a user would. */
function type(fixture: ComponentFixture<NewVersionPage>, id: string, value: string): void {
  const element = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(`#${id}`)!;
  element.value = value;
  element.dispatchEvent(new Event('input'));
  element.dispatchEvent(new Event('blur'));
  fixture.detectChanges();
}

describe('NewVersionPage', () => {
  it('says which ordinal the registration would take', () => {
    const { host } = render({ nextOrdinal: 4 });

    expect(host.textContent).toContain('v4');
  });

  describe('the form', () => {
    it('refuses to submit while the schema is empty', () => {
      const { host } = render();

      expect(host.querySelector('button[type="submit"]')?.hasAttribute('disabled')).toBe(true);
    });

    it('complains about malformed JSON before spending a request on it', () => {
      // A missing brace is a certain 400. Whether it is a *valid* schema is the registry's
      // question and it stays the registry's question — `ajv` (M4.4) narrows the gap, but
      // this one check is worth making without it.
      const { fixture, host } = render();
      type(fixture, 'schema', '{"type": ');

      expect(host.textContent).toContain('Not valid JSON');
      expect(host.querySelector('button[type="submit"]')?.hasAttribute('disabled')).toBe(true);
    });

    it('says nothing about an empty field nobody has touched', () => {
      // Complaining the moment the screen opens is telling someone off for not having started.
      const { host } = render();

      expect(host.textContent).not.toContain('Not valid JSON');
    });

    it('accepts a well-formed document', () => {
      const { fixture, host } = render();
      type(fixture, 'schema', '{"type":"object"}');

      expect(host.textContent).not.toContain('Not valid JSON');
      expect(host.querySelector('button[type="submit"]')?.hasAttribute('disabled')).toBe(false);
    });

    it('disables the button while a registration is in flight', () => {
      const { fixture, host } = render({ submitting: true });
      type(fixture, 'schema', '{"type":"object"}');

      expect(host.querySelector('button[type="submit"]')?.hasAttribute('disabled')).toBe(true);
      expect(host.textContent).toContain('Registering…');
    });
  });

  describe('the outcome', () => {
    it('reports a plain registration as registered', () => {
      const { host } = render({ outcome: outcome() });

      expect(host.textContent).toContain('Registered');
      expect(host.textContent).toContain('latest');
    });

    it('reports a breaking change as held, and says the pointer did not move', () => {
      // The distinction the whole screen exists for. This is a success with a consequence,
      // not a failure — and the reader has to be told that `latest` is unchanged, or they
      // will assume their consumers are already seeing it.
      const { host } = render({
        outcome: outcome({
          status: 'AWAITING_APPROVAL',
          divergences: [
            {
              path: '/properties/discount',
              kind: 'required_field_added',
              direction: 'BACKWARD',
              surface: 'WIRE_JSON',
              message: 'A required field was added.',
              conflictsWithVersion: 1,
            },
          ],
        }),
      });

      expect(host.textContent).toContain('Awaiting approval');
      expect(host.textContent).toContain('has not moved');
      expect(host.querySelector('cd-divergence-list')).not.toBeNull();
    });

    it('reports an unchanged document as unchanged, not as a new version', () => {
      // 200 rather than 201, and `created: false` is what carries it. Saying "registered"
      // here would have someone believe an ordinal was allocated that was not.
      const { host } = render({ outcome: outcome({ created: false, ordinal: 1 }) });

      expect(host.textContent).toContain('Unchanged');
      expect(host.textContent).toContain('no ordinal was allocated');
      expect(host.textContent).not.toContain('Registered as');
    });

    it('replaces the form rather than sitting above it', () => {
      // An editable form under a "registered" banner invites a second submit that either
      // allocates another ordinal or silently no-ops, and neither is what the button offers.
      const { host } = render({ outcome: outcome() });

      expect(host.querySelector('form')).toBeNull();
    });

    it('links to the version it just created', () => {
      const { host } = render({ outcome: outcome({ ordinal: 7 }) });

      expect(host.querySelector('a[href="/subjects/orders.created/versions/7"]')).not.toBeNull();
    });

    it('shows portability warnings as advice, not as a failure', () => {
      // Anything that reaches this list registered successfully — a portability finding
      // severe enough to be an error refuses the registration outright.
      const { host } = render({
        outcome: outcome({
          portability: [
            {
              path: '/properties/amount',
              kind: 'big_decimal',
              message: 'Python will read this as a float.',
            },
          ],
        }),
      });

      expect(host.textContent).toContain('Portability warnings');
      expect(host.textContent).toContain('Python will read this as a float.');
      expect(host.textContent).toContain('Registered');
    });
  });

  it('shows a refusal as an error, with the form still there to correct', () => {
    const { host } = render({
      error: new ConcordatError({
        status: 400,
        code: 'schema_invalid',
        detail: 'Not a valid JSON Schema: /type must be a string.',
      }),
    });

    expect(host.textContent).toContain('Not a valid JSON Schema: /type must be a string.');
    expect(host.querySelector('form')).not.toBeNull();
  });
});
