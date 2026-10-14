import { signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ThemeStore } from '../../../core/config/theme-store';
import { ConcordatError } from '../../../core/http/problem-details';
import type { RegistrationOutcome } from '../../../domain/registry/registration';
import { parseSemanticVersion, suggestLabels } from '../../../domain/registry/semantic-version';
import type { SchemaVersion } from '../../../domain/registry/subject';
import type { SchemaFormat } from '../../../domain/registry/wire-tokens';
import type { SchemaCheckRequest, SchemaCheckResult } from '../../../domain/registry/schema-check';
import { NewVersionStore } from '../application/new-version-store';
import { SchemaCheckStore } from '../application/schema-check-store';
import { MONACO_LOADER } from '../ui/schema-editor';
import { NewVersionPage } from './new-version-page';

// The screen's job is to say which of three things happened, and the three are genuinely
// different: registered, accepted-but-held, and already-the-tip. Conflating the middle one
// with a failure would have people re-submitting a change that already landed; conflating the
// third with the first would have them believe they created a version they did not.
//
// The other half is the form before that: what it will and will not let through, and the
// two-click register. A block here must be a *certain* refusal by the registry — a check that
// is stricter than the registry stops someone registering a document it would have taken.

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

function version(
  ordinal: number,
  semanticVersion: string | null = null,
  status: SchemaVersion['status'] = 'ACTIVE',
): SchemaVersion {
  return {
    ordinal,
    schemaId: `schema-${ordinal}`,
    semanticVersion,
    status,
    changelog: null,
    registeredAt: new Date('2026-10-13T09:00:00Z'),
    registeredBy: 'ci',
    deprecated: false,
  };
}

interface Scenario {
  outcome?: RegistrationOutcome | null;
  submitting?: boolean;
  error?: ConcordatError | null;
  nextOrdinal?: number | null;
  format?: SchemaFormat | null;
  versions?: readonly SchemaVersion[];
  latest?: number | null;
  liveLabel?: string | null;
  cloneError?: ConcordatError | null;
}

function storeIn(state: Scenario) {
  const value = state.outcome ?? null;
  const format = state.format === undefined ? 'json' : state.format;
  const versions = state.versions ?? [version(1)];
  const label = state.liveLabel === undefined ? null : state.liveLabel;
  const parsed = label === null ? null : parseSemanticVersion(label);
  const live = parsed?.ok ? parsed.value : null;

  const outcome = signal(value);

  return {
    // Present once the subject has loaded; the form refuses to send before that.
    subject: signal(format === null ? null : { name: 'orders.created', format }),
    format: signal(format),
    loading: signal(false),
    submitting: signal(state.submitting ?? false),
    outcome,
    error: signal(state.error ?? null),
    nextOrdinal: signal(state.nextOrdinal === undefined ? 2 : state.nextOrdinal),
    held: signal(value?.status === 'AWAITING_APPROVAL'),
    startingPoints: signal([...versions].sort((a, b) => b.ordinal - a.ordinal)),
    defaultStartingOrdinal: signal(state.latest === undefined ? 1 : state.latest),
    liveLabel: signal(live),
    labelSuggestions: signal(live === null ? null : suggestLabels(live)),
    cloning: signal(false),
    cloned: signal(null),
    cloneError: signal(state.cloneError ?? null),
    loadSubject: () => {},
    cloneFrom: vi.fn(),
    submit: vi.fn(),
    reset: () => outcome.set(null),
  } as unknown as InstanceType<typeof NewVersionStore> & {
    submit: ReturnType<typeof vi.fn>;
    cloneFrom: ReturnType<typeof vi.fn>;
  };
}

function checksIn() {
  const request = signal<SchemaCheckRequest | null>(null);

  return {
    checking: signal(false),
    request,
    result: signal<SchemaCheckResult | null>(null),
    failure: signal<string | null>(null),
    run: vi.fn((asked: SchemaCheckRequest) => request.set(asked)),
    clear: vi.fn(),
  };
}

function render(scenario: Scenario = {}): {
  fixture: ComponentFixture<NewVersionPage>;
  host: HTMLElement;
  store: ReturnType<typeof storeIn>;
  checks: ReturnType<typeof checksIn>;
} {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      provideConcordatConfig({ defaultEnvironment: 'dev' }),
      // Monaco never arrives in these tests: there is no layout engine to give it a size, and
      // the textarea it would replace is what the form is being tested through.
      { provide: MONACO_LOADER, useValue: () => new Promise(() => {}) },
      // The real store watches `matchMedia`, which jsdom does not have, and the page only reads
      // which theme is in force.
      { provide: ThemeStore, useValue: { resolved: signal('dark') } },
    ],
  });

  const store = storeIn(scenario);
  const checks = checksIn();

  TestBed.overrideComponent(NewVersionPage, {
    set: {
      providers: [
        { provide: NewVersionStore, useValue: store },
        { provide: SchemaCheckStore, useValue: checks },
      ],
    },
  });

  const fixture = TestBed.createComponent(NewVersionPage);
  fixture.componentRef.setInput('name', 'orders.created');
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, store, checks };
}

/** Types into a control and lets the form react, as a user would. */
function type(fixture: ComponentFixture<NewVersionPage>, id: string, value: string): void {
  const element = (fixture.nativeElement as HTMLElement).querySelector<
    HTMLInputElement | HTMLTextAreaElement
  >(`#${id}`)!;
  element.value = value;
  element.dispatchEvent(new Event('input'));
  element.dispatchEvent(new Event('blur'));
  fixture.detectChanges();
}

function click(fixture: ComponentFixture<NewVersionPage>, text: string): void {
  const button = [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')].find((b) =>
    b.textContent?.includes(text),
  );

  if (button === undefined) {
    throw new Error(`no button containing '${text}'`);
  }

  button.click();
  fixture.detectChanges();
}

const hasButton = (host: HTMLElement, text: string) =>
  [...host.querySelectorAll('button')].some((b) => b.textContent?.includes(text));

const submitButton = (host: HTMLElement) =>
  host.querySelector<HTMLButtonElement>('button[type="submit"]');

const VALID = '{"type":"object"}';

describe('NewVersionPage', () => {
  it('says which ordinal the registration would take', () => {
    const { host } = render({ nextOrdinal: 4 });

    expect(host.textContent).toContain('v4');
  });

  describe('the form', () => {
    it('refuses to submit while the schema is empty', () => {
      const { host } = render();

      expect(submitButton(host)?.disabled).toBe(true);
    });

    it('complains about malformed JSON before spending a request on it', () => {
      // A missing brace is a certain 400, so this one is worth catching in the browser.
      const { fixture, host } = render();
      type(fixture, 'schema', '{"type": ');

      expect(host.textContent).toContain('Not well-formed JSON');
      expect(host.textContent).toContain('The registry will refuse this');
      expect(submitButton(host)?.disabled).toBe(true);
    });

    it('refuses a duplicate key, which JSON.parse would have let through', () => {
      const { fixture, host } = render();
      type(fixture, 'schema', '{"type":"object","type":"string"}');

      expect(host.textContent).toContain("Duplicate object key 'type'");
      expect(submitButton(host)?.disabled).toBe(true);
    });

    it('refuses another JSON Schema dialect, naming it', () => {
      const { fixture, host } = render();
      type(fixture, 'schema', '{"$schema":"http://json-schema.org/draft-07/schema#"}');

      expect(host.textContent).toContain('draft-07');
      expect(host.textContent).toContain('2020-12');
      expect(submitButton(host)?.disabled).toBe(true);
    });

    it('does not read a Protobuf file as JSON', () => {
      // The reason `format` gates every check: calling a valid .proto malformed would refuse
      // every Protobuf schema the registry would take.
      const { fixture, host } = render({ format: 'protobuf' });
      type(fixture, 'schema', 'syntax = "proto3"; message M { string id = 1; }');

      expect(host.textContent).not.toContain('Not well-formed JSON');
      expect(submitButton(host)?.disabled).toBe(false);
    });

    it('does not apply the JSON Schema dialect rule to Avro', () => {
      const { fixture, host } = render({ format: 'avro' });
      type(fixture, 'schema', '{"type":"record","name":"R","fields":[],"$schema":"x"}');

      expect(host.textContent).not.toContain('Dialect');
      expect(submitButton(host)?.disabled).toBe(false);
    });

    it('says nothing about an empty field nobody has touched', () => {
      // Complaining the moment the screen opens is telling someone off for not having started.
      const { host } = render();

      expect(host.textContent).not.toContain('Not well-formed JSON');
      expect(host.textContent).not.toContain('required');
    });

    it('accepts a well-formed document', () => {
      const { fixture, host } = render();
      type(fixture, 'schema', VALID);

      expect(host.textContent).not.toContain('Not well-formed JSON');
      expect(submitButton(host)?.disabled).toBe(false);
    });

    it('will not send before the subject has loaded, because it cannot yet say what format it is', () => {
      const { fixture, host } = render({ format: null });
      type(fixture, 'schema', VALID);

      expect(submitButton(host)?.disabled).toBe(true);
    });

    it('disables the button while a registration is in flight', () => {
      const { fixture, host } = render({ submitting: true });
      type(fixture, 'schema', VALID);

      expect(submitButton(host)?.disabled).toBe(true);
      expect(host.textContent).toContain('Registering…');
    });
  });

  describe('registering takes two clicks', () => {
    // A version is permanent: the ordinal is never reused, the document cannot be edited and
    // there is no delete. The first click asks, naming what will be allocated; the second does it.

    it('does not register on the first click', () => {
      const { fixture, host, store } = render({ nextOrdinal: 3 });
      type(fixture, 'schema', VALID);
      click(fixture, 'Register');

      expect(store.submit).not.toHaveBeenCalled();
      expect(host.textContent).toContain('Register this as');
      expect(host.textContent).toContain('v3');
      expect(host.textContent).toContain('permanent');
    });

    it('registers on the second, with exactly what was confirmed', () => {
      const { fixture, store } = render();
      type(fixture, 'schema', `  ${VALID}  `);
      type(fixture, 'changelog', ' Added a field. ');
      click(fixture, 'Register');
      click(fixture, 'Yes, register it');

      expect(store.submit).toHaveBeenCalledTimes(1);
      expect(store.submit).toHaveBeenCalledWith({
        subject: 'orders.created',
        schema: VALID,
        semanticVersion: null,
        changelog: 'Added a field.',
      });
    });

    it('withdraws the confirmation when anything is edited afterwards', () => {
      // Otherwise the second click would send something the first never showed.
      const { fixture, host, store } = render();
      type(fixture, 'schema', VALID);
      click(fixture, 'Register');

      type(fixture, 'changelog', 'changed my mind');

      expect(host.textContent).not.toContain('Register this as');
      expect(store.submit).not.toHaveBeenCalled();
    });

    it('lets the author back out', () => {
      const { fixture, host, store } = render();
      type(fixture, 'schema', VALID);
      click(fixture, 'Register');
      click(fixture, 'Keep editing');

      expect(host.textContent).not.toContain('Register this as');
      expect(store.submit).not.toHaveBeenCalled();
    });

    it('does not ask about a document the registry would refuse', () => {
      const { fixture, host } = render();
      type(fixture, 'schema', '{"type": ');
      submitButton(host)?.click();
      fixture.detectChanges();

      expect(host.textContent).not.toContain('Register this as');
    });

    it('names the label that will be sent', () => {
      const { fixture, host } = render();
      type(fixture, 'schema', VALID);
      type(fixture, 'semanticVersion', '1.1.0');
      click(fixture, 'Register');

      expect(host.textContent).toContain('1.1.0');
    });
  });

  describe('registering another', () => {
    it('keeps the document to iterate on, and drops the label and changelog it belonged to', () => {
      // A label cannot be used twice, so carrying it over would set up the next registration
      // to be refused. The document is nearly always the starting point for the next one.
      const { fixture, host, store } = render({ liveLabel: '1.0.0' });
      type(fixture, 'schema', VALID);
      type(fixture, 'semanticVersion', '1.0.1');
      type(fixture, 'changelog', 'Added a field.');

      // The registry's answer arrives and replaces the form.
      (store.outcome as unknown as { set(value: RegistrationOutcome): void }).set(outcome());
      fixture.detectChanges();

      expect(host.querySelector('form')).toBeNull();

      click(fixture, 'Register another');

      expect(host.querySelector<HTMLTextAreaElement>('#schema')?.value).toBe(VALID);
      expect(host.querySelector<HTMLInputElement>('#semanticVersion')?.value).toBe('');
      expect(host.querySelector<HTMLInputElement>('#changelog')?.value).toBe('');
    });
  });

  describe('the semantic version label', () => {
    it('offers the three bumps from the highest live label', () => {
      const { host } = render({ liveLabel: '1.4.2' });

      for (const next of ['1.4.3', '1.5.0', '2.0.0']) {
        expect(hasButton(host, next)).toBe(true);
      }
    });

    it('puts a chosen suggestion in the field and sends it', () => {
      const { fixture, host, store } = render({ liveLabel: '1.4.2' });
      type(fixture, 'schema', VALID);
      click(fixture, '1.5.0');

      expect(host.querySelector<HTMLInputElement>('#semanticVersion')?.value).toBe('1.5.0');

      click(fixture, 'Register');
      click(fixture, 'Yes, register it');

      expect(store.submit).toHaveBeenCalledWith(
        expect.objectContaining({ semanticVersion: '1.5.0' }),
      );
    });

    it('leaves the field empty until somebody chooses', () => {
      // Offered, never applied: the right bump depends on a verdict only the registry has.
      const { host } = render({ liveLabel: '1.4.2' });

      expect(host.querySelector<HTMLInputElement>('#semanticVersion')?.value).toBe('');
    });

    it('offers a first release, and only that, when nothing live is labelled', () => {
      const { host } = render({ liveLabel: null });

      expect(hasButton(host, '1.0.0')).toBe(true);
      expect(host.textContent).toContain('No labelled version yet');
    });

    it('blocks a label that is not MAJOR.MINOR.PATCH, which the registry certainly refuses', () => {
      const { fixture, host } = render();
      type(fixture, 'schema', VALID);
      type(fixture, 'semanticVersion', 'v2');

      expect(host.textContent).toContain('Expected MAJOR.MINOR.PATCH');
      expect(submitButton(host)?.disabled).toBe(true);
    });

    it('blocks build metadata, naming why', () => {
      const { fixture, host } = render();
      type(fixture, 'schema', VALID);
      type(fixture, 'semanticVersion', '1.0.0+build');

      expect(host.textContent).toContain('Build metadata is not supported');
      expect(submitButton(host)?.disabled).toBe(true);
    });

    it('warns about a label that does not increase, without blocking it', () => {
      // The page may be older than the registry: a held version rejected since would have
      // freed the number. Advice that can veto a submit is wrong in exactly that direction.
      const { fixture, host } = render({ liveLabel: '2.0.0' });
      type(fixture, 'schema', VALID);
      type(fixture, 'semanticVersion', '1.0.1');

      expect(host.textContent).toContain('does not increase on 2.0.0');
      expect(submitButton(host)?.disabled).toBe(false);
    });
  });

  describe('starting from an earlier version', () => {
    it('says there are no versions yet, and offers nothing to load', () => {
      const { host } = render({ versions: [], latest: null, nextOrdinal: 1 });

      expect(host.textContent).toContain('No versions yet');
      expect(host.textContent).toContain('v1');
      expect(host.querySelector('#starting-point')).toBeNull();
    });

    it('lists the versions, with the current one chosen', () => {
      const { host } = render({
        versions: [version(1), version(2), version(3, null, 'AWAITING_APPROVAL')],
        latest: 2,
      });
      const select = host.querySelector<HTMLSelectElement>('#starting-point')!;

      expect([...select.options].map((o) => o.textContent?.trim().split(' ')[0])).toEqual([
        'v3',
        'v2',
        'v1',
      ]);
      expect(select.value).toBe('2');
    });

    it('loads the chosen version straight away when the editor is empty', () => {
      const { fixture, store } = render({ versions: [version(1), version(2)], latest: 2 });
      click(fixture, 'Load into the editor');

      expect(store.cloneFrom).toHaveBeenCalledWith(2);
    });

    it('asks before replacing what has been written, and only then replaces it', () => {
      // Loading discards work nothing here can bring back, so it takes a second click.
      const { fixture, host, store } = render({ versions: [version(1)], latest: 1 });
      type(fixture, 'schema', VALID);
      click(fixture, 'Load into the editor');

      expect(store.cloneFrom).not.toHaveBeenCalled();
      expect(host.textContent).toContain('This replaces what you have written');

      click(fixture, 'Replace');

      expect(store.cloneFrom).toHaveBeenCalledWith(1);
    });

    it('keeps what was written when the author declines', () => {
      const { fixture, host, store } = render({ versions: [version(1)], latest: 1 });
      type(fixture, 'schema', VALID);
      click(fixture, 'Load into the editor');
      click(fixture, 'Keep mine');

      expect(store.cloneFrom).not.toHaveBeenCalled();
      expect(host.querySelector<HTMLTextAreaElement>('#schema')?.value).toBe(VALID);
    });

    it('says when a load failed, in the registry words', () => {
      const { host } = render({
        cloneError: new ConcordatError({
          status: 404,
          code: 'schema_not_found',
          detail: 'No such schema.',
        }),
      });

      expect(host.textContent).toContain('No such schema.');
    });
  });

  describe('Validate', () => {
    const checked = (
      schema: string,
      overrides: Partial<SchemaCheckResult> = {},
    ): SchemaCheckResult => ({
      checked: schema,
      skipped: null,
      advice: [],
      payload: null,
      ...overrides,
    });

    it('is offered for JSON Schema once there is something to check', () => {
      const { fixture, host } = render();

      expect(hasButton(host, 'Validate')).toBe(false);

      type(fixture, 'schema', VALID);

      expect(hasButton(host, 'Validate')).toBe(true);
    });

    it('is absent for Avro and Protobuf, which are not JSON Schema', () => {
      // ajv speaks JSON Schema. Offering to validate an Avro record with it would answer a
      // question nobody asked, and badly.
      for (const format of ['avro', 'protobuf'] as const) {
        TestBed.resetTestingModule();

        const { fixture, host } = render({ format });
        type(fixture, 'schema', format === 'avro' ? '{"type":"record"}' : 'syntax = "proto3";');

        expect(hasButton(host, 'Validate')).toBe(false);
        expect(host.querySelector('#sample')).toBeNull();
      }
    });

    it('asks for the schema as it would be sent, with no sample when none is written', () => {
      const { fixture, checks } = render();
      type(fixture, 'schema', `  ${VALID}  `);
      click(fixture, 'Validate');

      expect(checks.run).toHaveBeenCalledWith({ schema: VALID, payload: null });
    });

    it('sends the sample message along, trimmed', () => {
      const { fixture, checks } = render();
      type(fixture, 'schema', VALID);
      type(fixture, 'sample', ' {"id":"a"} ');
      click(fixture, 'Validate');

      expect(checks.run).toHaveBeenCalledWith({ schema: VALID, payload: '{"id":"a"}' });
    });

    it('shows what ajv said, placed in the text, as advice', () => {
      const { fixture, host, checks } = render();
      const schema = '{"type":"bogus"}';
      type(fixture, 'schema', schema);
      checks.request.set({ schema, payload: null });
      checks.result.set(
        checked(schema, {
          advice: [{ code: 'ajv_metaschema', pointer: '/type', message: '/type must be one of.' }],
        }),
      );
      fixture.detectChanges();

      expect(host.textContent).toContain('Worth checking');
      expect(host.textContent).toContain('/type must be one of.');
      // `"bogus"` starts at column 9 of the document.
      expect(host.textContent).toContain('line 1, column 9');
    });

    it('never blocks a registration, because it is a different engine’s reading', () => {
      // The registry refuses a new {"type":"bogus"} (schema_invalid, ADR-039), and this still does
      // not block: ajv is not the registry's engine, so a block that was wrong would be wrong in
      // the strict direction. The registry's own answer is what the submit then shows.
      const { fixture, host, checks } = render();
      const schema = '{"type":"bogus"}';
      type(fixture, 'schema', schema);
      checks.request.set({ schema, payload: null });
      checks.result.set(
        checked(schema, {
          advice: [{ code: 'ajv_metaschema', pointer: '/type', message: 'not valid.' }],
        }),
      );
      fixture.detectChanges();

      expect(submitButton(host)?.disabled).toBe(false);
    });

    it('hides an answer about text that has since changed, and says so', () => {
      // An answer about a document that was edited is an answer about something else.
      const { fixture, host, checks } = render();
      const schema = '{"type":"bogus"}';
      type(fixture, 'schema', schema);
      checks.request.set({ schema, payload: null });
      checks.result.set(
        checked(schema, {
          advice: [{ code: 'ajv_metaschema', pointer: '/type', message: 'not valid.' }],
        }),
      );
      fixture.detectChanges();

      type(fixture, 'schema', '{"type":"string"}');

      expect(host.textContent).not.toContain('not valid.');
      expect(host.textContent).toContain('changed since the last check');
    });

    it('says a check that could not run is not an answer, and that the registry still decides', () => {
      const { fixture, host, checks } = render();
      type(fixture, 'schema', VALID);
      checks.failure.set('The check took too long and was stopped.');
      fixture.detectChanges();

      expect(host.textContent).toContain('Could not check here');
      expect(host.textContent).toContain('The registry will still judge the schema');
      expect(submitButton(host)?.disabled).toBe(false);
    });

    it('says when the schema itself was not looked at, rather than that nothing was wrong', () => {
      const { fixture, host, checks } = render();
      type(fixture, 'schema', VALID);
      checks.request.set({ schema: VALID, payload: null });
      checks.result.set(checked(VALID, { skipped: 'Dialect is not 2020-12.' }));
      fixture.detectChanges();

      expect(host.textContent).toContain('The schema was not checked');
      expect(host.textContent).not.toContain('Nothing to flag');
    });

    it('reports a sample message that does not conform', () => {
      const { fixture, host, checks } = render();
      type(fixture, 'schema', VALID);
      type(fixture, 'sample', '{"id":1}');
      checks.request.set({ schema: VALID, payload: '{"id":1}' });
      checks.result.set(
        checked(VALID, {
          payload: {
            kind: 'invalid',
            errors: [{ path: '/id', message: 'must be string' }],
            omitted: 0,
          },
        }),
      );
      fixture.detectChanges();

      expect(host.textContent).toContain('does not conform');
      expect(host.textContent).toContain('/id');
      expect(host.textContent).toContain('must be string');
    });
  });

  describe('Format', () => {
    it('lays the document out without touching a number literal', () => {
      // `1.0` and `1` are different schemas to the registry; reformatting through JSON.parse
      // would silently turn one into the other.
      const { fixture, host } = render();
      type(fixture, 'schema', '{"type":"object","maximum":1.0}');
      click(fixture, 'Format');

      const text = host.querySelector<HTMLTextAreaElement>('#schema')!.value;

      expect(text).toBe('{\n  "type": "object",\n  "maximum": 1.0\n}');
    });

    it('is absent for Protobuf, which is not JSON', () => {
      const { fixture, host } = render({ format: 'protobuf' });
      type(fixture, 'schema', 'syntax = "proto3";');

      expect(hasButton(host, 'Format')).toBe(false);
    });

    it('is absent when there is nothing to format', () => {
      const { host } = render();

      expect(hasButton(host, 'Format')).toBe(false);
    });

    it('says why it did nothing when the document does not read, and leaves it alone', () => {
      const { fixture, host } = render();
      type(fixture, 'schema', '{"type": ');
      click(fixture, 'Format');

      expect(host.textContent).toContain('nothing to lay out');
      expect(host.querySelector<HTMLTextAreaElement>('#schema')?.value).toBe('{"type": ');
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
