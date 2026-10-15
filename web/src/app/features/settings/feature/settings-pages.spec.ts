import { signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';
import { SessionStore } from '../../../core/auth/session-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { Broker, Environment } from '../../../domain/environments/environment';
import type { Scope } from '../../../domain/identity/scope';
import {
  COMPATIBILITY_MODE_MEANING,
  COMPATIBILITY_MODES,
  COMPATIBILITY_SURFACE_MEANING,
  COMPATIBILITY_SURFACES,
} from '../../../domain/registry/compatibility';
import { EnvironmentStore } from '../application/environment-store';
import { BrokersPage } from './brokers-page';
import { changesBetween, EnvironmentSettingsPage } from './environment-settings-page';
import { SettingsNav } from './settings-nav';

// The screens' own decisions: that nothing is sent that did not change, that every control is
// absent without its scope, that a credential form never shows a credential and empties itself,
// and that a destructive action takes two clicks.

function broker(overrides: Partial<Broker> = {}): Broker {
  return {
    brokerId: 'b1',
    displayName: 'primary',
    uri: 'amqps://rabbit.example.com:5671',
    virtualHost: '/',
    useTls: true,
    hasCredentials: true,
    status: 'REACHABLE',
    lastCheckedAt: new Date('2026-08-13T09:00:00Z'),
    lastError: null,
    ...overrides,
  };
}

function environment(overrides: Partial<Environment> = {}): Environment {
  return {
    name: 'dev',
    description: 'Shared development',
    defaultCompatibility: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
    registrationPolicy: 'OPEN',
    allowPreReleaseVersions: false,
    createdAt: new Date('2026-08-01T09:00:00Z'),
    brokers: [broker()],
    ...overrides,
  };
}

interface State {
  environment?: Environment | null;
  loading?: boolean;
  error?: ConcordatError | null;
  saving?: string | null;
  actionErrors?: Record<string, ConcordatError>;
  lastSaved?: string | null;
}

function provideStore(state: State) {
  const calls = {
    update: vi.fn(),
    addBroker: vi.fn(),
    checkBroker: vi.fn(),
    setCredential: vi.fn(),
    removeCredential: vi.fn(),
    removeBroker: vi.fn(),
    load: vi.fn(),
  };

  const lastSaved = signal(state.lastSaved ?? null);

  return {
    calls,
    lastSaved,
    provider: {
      provide: EnvironmentStore,
      useValue: {
        environment: signal('environment' in state ? state.environment : environment()),
        loading: signal(state.loading ?? false),
        error: signal(state.error ?? null),
        saving: signal(state.saving ?? null),
        actionErrors: signal(state.actionErrors ?? {}),
        lastSaved,
        ...calls,
      },
    },
  };
}

function renderEnvironment(state: State = {}, scopes: readonly Scope[] = ['env:write']) {
  const { calls, provider } = provideStore(state);

  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });
  TestBed.overrideComponent(EnvironmentSettingsPage, { set: { providers: [provider] } });
  TestBed.inject(SessionStore).signIn({ credential: 'c', actor: 'alice', scopes });

  const fixture = TestBed.createComponent(EnvironmentSettingsPage);
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, ...calls };
}

function renderBrokers(
  state: State = {},
  scopes: readonly Scope[] = ['broker:write'],
): { fixture: ComponentFixture<BrokersPage>; host: HTMLElement } & ReturnType<
  typeof provideStore
>['calls'] & { lastSaved: ReturnType<typeof provideStore>['lastSaved'] } {
  const { calls, provider, lastSaved } = provideStore(state);

  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });
  TestBed.overrideComponent(BrokersPage, { set: { providers: [provider] } });
  // The rows inject the store their page provides; give them the same fake.
  TestBed.overrideProvider(EnvironmentStore, provider);
  TestBed.inject(SessionStore).signIn({ credential: 'c', actor: 'alice', scopes });

  const fixture = TestBed.createComponent(BrokersPage);
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, lastSaved, ...calls };
}

function fill(host: HTMLElement, id: string, value: string | boolean): void {
  const element = host.querySelector(`#${id}`) as HTMLInputElement | HTMLSelectElement;

  if (typeof value === 'boolean') {
    (element as HTMLInputElement).checked = value;
    element.dispatchEvent(new Event('change'));
    return;
  }

  element.value = value;
  element.dispatchEvent(new Event(element instanceof HTMLSelectElement ? 'change' : 'input'));
}

function button(host: HTMLElement, text: string): HTMLButtonElement | undefined {
  return [...host.querySelectorAll('button')].find((b) => b.textContent?.trim() === text);
}

describe('changesBetween', () => {
  const current = environment();
  const same = {
    description: 'Shared development',
    mode: 'BACKWARD',
    surface: 'WIRE_JSON',
    policy: 'OPEN',
    preRelease: false,
  };

  it('is empty when nothing differs', () => {
    expect(changesBetween(current, same)).toEqual({});
  });

  it('names only the member that changed, so saving one field restates none of the others', () => {
    expect(changesBetween(current, { ...same, description: 'new' })).toEqual({
      description: 'new',
    });
  });

  it('maps each form field to the registry’s own member name', () => {
    expect(
      changesBetween(current, {
        description: 'x',
        mode: 'FULL',
        surface: 'SOURCE',
        policy: 'CI_ONLY',
        preRelease: true,
      }),
    ).toEqual({
      description: 'x',
      compatibilityMode: 'FULL',
      compatibilitySurface: 'SOURCE',
      registrationPolicy: 'CI_ONLY',
      allowPreReleaseVersions: true,
    });
  });

  // The registry answers a lone member with a 400, "needs both a mode and a surface" (ADR-016),
  // measured against the real thing: mode alone, surface alone and a mode with a null surface were
  // each refused and stored nothing, while both, both null and an unchanged pair were 200. So the
  // two compatibility members go together or not at all.
  it('sends the surface as stored when only the mode changed, because the registry refuses half a policy', () => {
    expect(changesBetween(current, { ...same, mode: 'FULL' })).toEqual({
      compatibilityMode: 'FULL',
      compatibilitySurface: 'WIRE_JSON',
    });
  });

  it('sends the mode as stored when only the surface changed', () => {
    expect(changesBetween(current, { ...same, surface: 'SOURCE' })).toEqual({
      compatibilityMode: 'BACKWARD',
      compatibilitySurface: 'SOURCE',
    });
  });

  it('sends the pair when both changed', () => {
    expect(changesBetween(current, { ...same, mode: 'NONE', surface: 'WIRE' })).toEqual({
      compatibilityMode: 'NONE',
      compatibilitySurface: 'WIRE',
    });
  });

  it('sends neither compatibility member when neither changed, whatever else did', () => {
    const changes = changesBetween(current, { ...same, description: 'new', preRelease: true });

    expect(changes).toEqual({ description: 'new', allowPreReleaseVersions: true });
    expect('compatibilityMode' in changes).toBe(false);
    expect('compatibilitySurface' in changes).toBe(false);
  });

  it('sends the pair beside a description that changed too, as one request', () => {
    expect(changesBetween(current, { ...same, description: 'new', mode: 'FULL' })).toEqual({
      description: 'new',
      compatibilityMode: 'FULL',
      compatibilitySurface: 'WIRE_JSON',
    });
  });

  it.each([
    ['the mode', { mode: '' }],
    ['the surface', { surface: '' }],
    ['both', { mode: '', surface: '' }],
  ])(
    'sends no half of the pair when %s is empty, which would be a 400 about a value nobody picked',
    (_which, empty) => {
      // The form is empty until it is reset to the environment on arrival.
      const changes = changesBetween(current, { ...same, ...empty });

      expect('compatibilityMode' in changes).toBe(false);
      expect('compatibilitySurface' in changes).toBe(false);
    },
  );

  it('sends false when pre-release is turned off, which is a change and not an omission', () => {
    const on = environment({ allowPreReleaseVersions: true });

    expect(changesBetween(on, { ...same, preRelease: false })).toEqual({
      allowPreReleaseVersions: false,
    });
  });
});

describe('EnvironmentSettingsPage', () => {
  it('shows the current settings as text for anyone', () => {
    const { host } = renderEnvironment({}, ['subject:read']);

    expect(host.textContent).toContain('OPEN');
    expect(host.textContent).toContain('BACKWARD · WIRE_JSON');
    expect(host.textContent).toContain('Not allowed');
    expect(host.textContent).toContain('Shared development');
  });

  describe('saying what the default compatibility means', () => {
    const sentence = (host: HTMLElement, id: string) =>
      host.querySelector<HTMLElement>(
        '#' + host.querySelector('#' + id)!.getAttribute('aria-describedby')!,
      )!;

    it('describes each select by the meaning of what it is on, as the choice changes', () => {
      const { fixture, host } = renderEnvironment();

      expect(sentence(host, 'env-mode').textContent?.trim()).toBe(
        COMPATIBILITY_MODE_MEANING.BACKWARD,
      );
      expect(sentence(host, 'env-surface').textContent?.trim()).toBe(
        COMPATIBILITY_SURFACE_MEANING.WIRE_JSON,
      );

      fill(host, 'env-mode', 'FULL_TRANSITIVE');
      fill(host, 'env-surface', 'SOURCE');
      fixture.detectChanges();

      expect(sentence(host, 'env-mode').textContent?.trim()).toBe(
        COMPATIBILITY_MODE_MEANING.FULL_TRANSITIVE,
      );
      expect(sentence(host, 'env-surface').textContent?.trim()).toBe(
        COMPATIBILITY_SURFACE_MEANING.SOURCE,
      );
    });

    it('leaves the selects named by their labels, which the e2e specs find them by', () => {
      const { host } = renderEnvironment();

      for (const [id, label] of [
        ['env-mode', 'Default compatibility mode'],
        ['env-surface', 'Default compatibility surface'],
      ] as const) {
        const select = host.querySelector<HTMLSelectElement>('#' + id)!;

        expect(select.labels?.[0]?.textContent?.trim()).toBe(label);
        expect(select.hasAttribute('aria-label')).toBe(false);
      }
    });

    it('explains the default it shows, to a reader too, and only that one', () => {
      const { fixture, host } = renderEnvironment(
        { environment: environment({ defaultCompatibility: { mode: 'FULL', surface: 'SOURCE' } }) },
        ['subject:read'],
      );
      const summary = host.querySelector<HTMLButtonElement>('dl + cd-compatibility-guide button')!;

      expect(summary.textContent).toContain('What does this default mean?');
      summary.click();
      fixture.detectChanges();

      const terms = [...host.querySelectorAll('dl + cd-compatibility-guide dt')].map((dt) =>
        dt.textContent?.trim(),
      );
      expect(terms).toEqual(['FULL', 'SOURCE']);
    });

    it('offers every word at once to somebody choosing, since a select shows one at a time', () => {
      const { fixture, host } = renderEnvironment();
      const form = host.querySelector<HTMLButtonElement>('form cd-compatibility-guide button')!;

      form.click();
      fixture.detectChanges();

      expect(
        [...host.querySelectorAll('form cd-compatibility-guide dt')].map((dt) =>
          dt.textContent?.trim(),
        ),
      ).toEqual([...COMPATIBILITY_MODES, ...COMPATIBILITY_SURFACES]);
    });
  });

  it('offers no "Not set" default, because the registry never has one (ADR-033)', () => {
    const { host } = renderEnvironment();

    for (const id of ['env-mode', 'env-surface']) {
      const options = [...host.querySelectorAll(`#${id} option`)].map((o) => o.textContent?.trim());

      expect(options, id).not.toContain('Not set');
      expect(options, id).not.toContain('');
    }
  });

  it('sends the whole default when only the mode is changed, which is the case the registry refuses by halves', () => {
    const { fixture, host, update } = renderEnvironment();

    fill(host, 'env-mode', 'FULL');
    fixture.detectChanges();

    expect(button(host, 'Save changes')!.disabled).toBe(false);

    host.querySelector('form')!.dispatchEvent(new Event('submit'));

    expect(update).toHaveBeenCalledWith({
      compatibilityMode: 'FULL',
      compatibilitySurface: 'WIRE_JSON',
    });
  });

  it('sends the whole default when only the surface is changed', () => {
    const { fixture, host, update } = renderEnvironment();

    fill(host, 'env-surface', 'SOURCE');
    fixture.detectChanges();
    host.querySelector('form')!.dispatchEvent(new Event('submit'));

    expect(update).toHaveBeenCalledWith({
      compatibilityMode: 'BACKWARD',
      compatibilitySurface: 'SOURCE',
    });
  });

  it('offers the form to someone with env:write', () => {
    expect(renderEnvironment().host.querySelector('#env-description')).not.toBeNull();
  });

  it.each([
    ['a reader', ['subject:read']],
    ['a broker admin', ['broker:write']],
  ] as const)('omits the form for %s, rather than disabling it (ADR-018)', (_who, scopes) => {
    expect(
      renderEnvironment({}, scopes as readonly Scope[]).host.querySelector('#env-description'),
    ).toBeNull();
  });

  it('starts with the form equal to the environment, so there is nothing to save', () => {
    const { host } = renderEnvironment();

    expect(button(host, 'Save changes')!.disabled).toBe(true);
    expect(host.textContent).toContain('Nothing has changed');
  });

  it('enables saving once something differs, and sends only that', () => {
    const { fixture, host, update } = renderEnvironment();

    fill(host, 'env-description', 'Staging for the payments team');
    fixture.detectChanges();

    expect(button(host, 'Save changes')!.disabled).toBe(false);

    host.querySelector('form')!.dispatchEvent(new Event('submit'));

    expect(update).toHaveBeenCalledWith({ description: 'Staging for the payments team' });
  });

  it('sends the pre-release toggle, which decision #8 left owing a checkbox', () => {
    const { fixture, host, update } = renderEnvironment();

    fill(host, 'env-prerelease', true);
    fixture.detectChanges();
    host.querySelector('form')!.dispatchEvent(new Event('submit'));

    expect(update).toHaveBeenCalledWith({ allowPreReleaseVersions: true });
  });

  it('explains what the chosen registration policy means', () => {
    const { fixture, host } = renderEnvironment();

    fill(host, 'env-policy', 'CI_ONLY');
    fixture.detectChanges();

    expect(host.textContent).toContain('A person cannot, however senior');
  });

  it('says a changed default takes effect at once for every subject that inherits it', () => {
    expect(renderEnvironment().host.textContent).toContain('every subject here that has not set');
  });

  it('says what “at once” is, and that what is already registered or held is left alone', () => {
    // True since ADR-033, and measured by `e2e/environment-default.spec.ts`: the next version
    // registered is judged by the new default, and versions already registered or held are not.
    const text = renderEnvironment().host.textContent ?? '';

    expect(text).toContain('the next version registered, checked or compared is judged by it');
    expect(text).toContain('Versions already registered, or held at the approval gate');
  });

  it('shows the registry’s refusal under the form', () => {
    const { host } = renderEnvironment({
      actionErrors: {
        settings: new ConcordatError({ status: 400, code: 'x', detail: 'Policy refused.' }),
      },
    });

    expect(host.textContent).toContain('Could not save the settings');
    expect(host.textContent).toContain('Policy refused.');
  });

  it('says when settings were saved', () => {
    expect(renderEnvironment({ lastSaved: 'settings' }).host.textContent).toContain(
      'Settings saved',
    );
  });

  it('renders a failed load with a retry', () => {
    const { host } = renderEnvironment({
      environment: null,
      error: new ConcordatError({ status: 404, code: 'x', detail: 'No such environment.' }),
    });

    expect(host.textContent).toContain('Could not load this environment');
  });
});

describe('BrokersPage', () => {
  it('lists each broker with its status, endpoint and whether a credential is stored', () => {
    const { host } = renderBrokers();

    expect(host.textContent).toContain('primary');
    expect(host.textContent).toContain('REACHABLE');
    expect(host.textContent).toContain('amqps://rabbit.example.com:5671');
    expect(host.textContent).toContain('credentials stored');
  });

  it('shows an unreachable broker with its own error, as a finding and not a failure', () => {
    const { host } = renderBrokers({
      environment: environment({
        brokers: [broker({ status: 'UNREACHABLE', lastError: 'connection refused' })],
      }),
    });

    expect(host.textContent).toContain('UNREACHABLE');
    expect(host.textContent).toContain('connection refused');
    expect(host.textContent).not.toContain('Could not check');
  });

  it('says a broker that was never checked was never checked', () => {
    const { host } = renderBrokers({
      environment: environment({ brokers: [broker({ lastCheckedAt: null, status: 'UNKNOWN' })] }),
    });

    expect(host.textContent).toContain('Never checked');
  });

  it('says what no brokers means, without implying something is wrong', () => {
    const { host } = renderBrokers({ environment: environment({ brokers: [] }) });

    expect(host.textContent).toContain('No brokers registered');
    expect(host.textContent).toContain('Nothing is wrong without one');
  });

  it('lets anyone check a connection', () => {
    const { host, checkBroker } = renderBrokers({}, ['broker:read']);

    button(host, 'Check connection')!.click();

    expect(checkBroker).toHaveBeenCalledWith('b1');
  });

  describe('who may change brokers', () => {
    it('offers every control to someone with broker:write', () => {
      const { host } = renderBrokers();

      expect(button(host, 'Replace credentials')).toBeDefined();
      expect(button(host, 'Remove broker')).toBeDefined();
      expect(host.querySelector('#broker-name')).not.toBeNull();
    });

    it('omits every one for a reader (ADR-018)', () => {
      const { host } = renderBrokers({}, ['broker:read']);

      expect(button(host, 'Replace credentials')).toBeUndefined();
      expect(button(host, 'Remove broker')).toBeUndefined();
      expect(button(host, 'Remove credentials')).toBeUndefined();
      expect(host.querySelector('#broker-name')).toBeNull();
    });
  });

  describe('adding a broker', () => {
    it('sends the trimmed fields', () => {
      const { fixture, host, addBroker } = renderBrokers();

      fill(host, 'broker-name', ' secondary ');
      fill(host, 'broker-uri', ' amqp://other:5672 ');
      fixture.detectChanges();
      host
        .querySelector('form[aria-labelledby="add-broker-heading"]')!
        .dispatchEvent(new Event('submit'));

      expect(addBroker).toHaveBeenCalledWith({
        displayName: 'secondary',
        uri: 'amqp://other:5672',
        virtualHost: '',
      });
    });

    it('sends nothing for a blank name or endpoint', () => {
      const { fixture, host, addBroker } = renderBrokers();

      fill(host, 'broker-name', '   ');
      fixture.detectChanges();
      host
        .querySelector('form[aria-labelledby="add-broker-heading"]')!
        .dispatchEvent(new Event('submit'));

      expect(addBroker).not.toHaveBeenCalled();
    });

    it('shows the registry’s refusal under the form, and keeps what was typed', () => {
      const { fixture, host } = renderBrokers({
        actionErrors: {
          'add-broker': new ConcordatError({ status: 409, code: 'x', detail: 'Name taken.' }),
        },
      });

      fill(host, 'broker-name', 'primary');
      fixture.detectChanges();

      expect(host.textContent).toContain('Could not add the broker');
      expect(host.textContent).toContain('Name taken.');
      expect((host.querySelector('#broker-name') as HTMLInputElement).value).toBe('primary');
    });

    it('empties the form once the broker is stored', () => {
      const { fixture, host, lastSaved } = renderBrokers();

      fill(host, 'broker-name', 'secondary');
      fixture.detectChanges();
      lastSaved.set('add-broker');
      fixture.detectChanges();

      expect((host.querySelector('#broker-name') as HTMLInputElement).value).toBe('');
    });
  });

  describe('credentials', () => {
    function open(
      host: HTMLElement,
      fixture: ComponentFixture<unknown>,
      text = 'Replace credentials',
    ) {
      button(host, text)!.click();
      fixture.detectChanges();
    }

    it('is closed until asked for', () => {
      expect(renderBrokers().host.querySelector('#pass-b1')).toBeNull();
    });

    it('offers “Set” rather than “Replace” when none is stored', () => {
      const { host } = renderBrokers({
        environment: environment({ brokers: [broker({ hasCredentials: false })] }),
      });

      expect(button(host, 'Set credentials')).toBeDefined();
      expect(button(host, 'Remove credentials')).toBeUndefined();
    });

    it('never shows a credential: the fields are always empty and say it is write-only', () => {
      const { fixture, host } = renderBrokers();
      open(host, fixture);

      expect((host.querySelector('#user-b1') as HTMLInputElement).value).toBe('');
      expect((host.querySelector('#pass-b1') as HTMLInputElement).value).toBe('');
      expect(host.textContent).toContain('Write-only');
    });

    it('keeps the password out of a password manager’s reach', () => {
      // Without `new-password` a manager offers to fill the broker's password with the one the
      // browser has saved for the registry itself.
      const { fixture, host } = renderBrokers();
      open(host, fixture);

      const password = host.querySelector('#pass-b1') as HTMLInputElement;

      expect(password.type).toBe('password');
      expect(password.getAttribute('autocomplete')).toBe('new-password');
    });

    it('sends the pair, and empties the password even before the registry answers', () => {
      const { fixture, host, setCredential } = renderBrokers();
      open(host, fixture);

      fill(host, 'user-b1', ' guest ');
      fill(host, 'pass-b1', 'hunter2');
      fixture.detectChanges();
      host
        .querySelector('form[aria-label="Credentials for primary"]')!
        .dispatchEvent(new Event('submit'));

      expect(setCredential).toHaveBeenCalledWith({
        brokerId: 'b1',
        username: 'guest',
        password: 'hunter2',
      });
      expect((host.querySelector('#pass-b1') as HTMLInputElement).value).toBe('');
    });

    it('sends nothing without a password', () => {
      const { fixture, host, setCredential } = renderBrokers();
      open(host, fixture);

      fill(host, 'user-b1', 'guest');
      fixture.detectChanges();
      host
        .querySelector('form[aria-label="Credentials for primary"]')!
        .dispatchEvent(new Event('submit'));

      expect(setCredential).not.toHaveBeenCalled();
    });

    it('closes and empties itself once the credential is stored', () => {
      const { fixture, host, lastSaved } = renderBrokers();
      open(host, fixture);

      fill(host, 'user-b1', 'guest');
      fixture.detectChanges();
      lastSaved.set('credentials:b1');
      fixture.detectChanges();

      expect(host.querySelector('#user-b1')).toBeNull();
    });

    it('is not closed by another broker’s credential being stored', () => {
      const { fixture, host, lastSaved } = renderBrokers();
      open(host, fixture);

      lastSaved.set('credentials:some-other-broker');
      fixture.detectChanges();

      expect(host.querySelector('#user-b1')).not.toBeNull();
    });

    it('shows a refusal beside the broker it belongs to', () => {
      const { host } = renderBrokers({
        actionErrors: {
          'credentials:b1': new ConcordatError({ status: 400, code: 'x', detail: 'Bad pair.' }),
        },
      });

      expect(host.textContent).toContain('Could not save the credentials');
      expect(host.textContent).toContain('Bad pair.');
    });
  });

  describe('removing', () => {
    it('takes two clicks to remove a broker, and names what goes', () => {
      const { fixture, host, removeBroker } = renderBrokers();

      button(host, 'Remove broker')!.click();
      fixture.detectChanges();

      expect(removeBroker).not.toHaveBeenCalled();
      expect(host.textContent).toContain('Bindings that name it stop applying');

      button(host, 'Confirm')!.click();

      expect(removeBroker).toHaveBeenCalledWith('b1');
    });

    it('takes two clicks to remove credentials, and says the next check will fail', () => {
      const { fixture, host, removeCredential } = renderBrokers();

      button(host, 'Remove credentials')!.click();
      fixture.detectChanges();

      expect(host.textContent).toContain('The next check will fail');

      button(host, 'Confirm')!.click();

      expect(removeCredential).toHaveBeenCalledWith('b1');
    });

    it('sends nothing when cancelled', () => {
      const { fixture, host, removeBroker } = renderBrokers();

      button(host, 'Remove broker')!.click();
      fixture.detectChanges();
      button(host, 'Cancel')!.click();
      fixture.detectChanges();

      expect(removeBroker).not.toHaveBeenCalled();
      expect(button(host, 'Remove broker')).toBeDefined();
    });
  });
});

describe('SettingsNav', () => {
  function render(scopes: readonly Scope[]): HTMLElement {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    TestBed.inject(SessionStore).signIn({ credential: 'c', actor: 'alice', scopes });
    const fixture = TestBed.createComponent(SettingsNav);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  const labels = (host: HTMLElement) =>
    [...host.querySelectorAll('a')].map((a) => a.textContent?.trim());

  it('shows every tab to an org admin', () => {
    expect(labels(render(['org:admin']))).toEqual([
      'Environment',
      'Brokers',
      'Members',
      'API keys',
      'Notifications',
      'Account',
    ]);
  });

  it('omits members and API keys for everyone else, since the server refuses them even to read', () => {
    // But not Account, which every signed-in person owns: a reader changes their own password.
    expect(labels(render(['subject:read']))).toEqual([
      'Environment',
      'Brokers',
      'Notifications',
      'Account',
    ]);
  });

  it('labels the navigation, so it is not confused with the main one', () => {
    expect(render(['org:admin']).querySelector('nav')?.getAttribute('aria-label')).toBe('Settings');
  });
});
