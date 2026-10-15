import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';
import { SessionStore } from '../../../core/auth/session-store';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { Scope } from '../../../domain/identity/scope';
import type { OutboxDepth, Subscription } from '../../../domain/notifications/subscription';
import { NotificationsStore } from '../application/notifications-store';
import { NotificationsPage } from './notifications-page';

// The screen's own decisions: that "none ticked" is said to mean every event, that a parked
// count is surfaced, that the webhook secret is shown once and stays until dismissed, and that
// every control is absent without env:write.

function subscription(overrides: Partial<Subscription> = {}): Subscription {
  return {
    id: 's1',
    channel: 'WEBHOOK',
    endpoint: 'https://hooks.example.com/x',
    events: [],
    enabled: true,
    createdAt: new Date('2026-08-13T09:00:00Z'),
    ...overrides,
  };
}

function render(
  state: {
    subscriptions?: readonly Subscription[];
    outbox?: OutboxDepth | null;
    loading?: boolean;
    isEmpty?: boolean;
    error?: ConcordatError | null;
    creating?: boolean;
    createError?: ConcordatError | null;
    created?: Subscription | null;
    signingSecret?: { endpoint: string; secret: string } | null;
    busy?: readonly string[];
    rowErrors?: Record<string, ConcordatError>;
  } = {},
  scopes: readonly Scope[] = ['env:write'],
) {
  const create = vi.fn();
  const setEnabled = vi.fn();
  const setEvents = vi.fn();
  const remove = vi.fn();
  const dismiss = vi.fn();
  const created = signal(state.created ?? null);

  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });
  TestBed.overrideComponent(NotificationsPage, {
    set: {
      providers: [
        {
          provide: NotificationsStore,
          useValue: {
            subscriptions: signal(state.subscriptions ?? [subscription()]),
            outbox: signal(state.outbox ?? null),
            loading: signal(state.loading ?? false),
            isEmpty: signal(state.isEmpty ?? false),
            error: signal(state.error ?? null),
            creating: signal(state.creating ?? false),
            createError: signal(state.createError ?? null),
            created,
            signingSecret: signal(state.signingSecret ?? null),
            busy: signal(state.busy ?? []),
            rowErrors: signal(state.rowErrors ?? {}),
            load: () => {},
            create,
            setEnabled,
            setEvents,
            remove,
            dismiss,
          },
        },
      ],
    },
  });
  TestBed.inject(SessionStore).signIn({ credential: 'c', actor: 'a', scopes });

  const fixture = TestBed.createComponent(NotificationsPage);
  fixture.detectChanges();

  return {
    fixture,
    host: fixture.nativeElement as HTMLElement,
    create,
    setEnabled,
    setEvents,
    remove,
    dismiss,
    created,
  };
}

function fill(host: HTMLElement, id: string, value: string): void {
  const element = host.querySelector(`#${id}`) as HTMLInputElement | HTMLSelectElement;
  element.value = value;
  element.dispatchEvent(new Event(element instanceof HTMLSelectElement ? 'change' : 'input'));
}

function button(host: HTMLElement, text: string): HTMLButtonElement | undefined {
  return [...host.querySelectorAll('button')].find((b) => b.textContent?.trim() === text);
}

function tick(host: HTMLElement, event: string): void {
  const box = [...host.querySelectorAll('fieldset label')]
    .find((l) => l.querySelector('.font-mono')?.textContent?.trim() === event)
    ?.querySelector('input') as HTMLInputElement;
  box.click();
}

describe('NotificationsPage', () => {
  describe('listing', () => {
    it('shows each subscription’s channel and endpoint', () => {
      const { host } = render();

      expect(host.textContent).toContain('WEBHOOK');
      expect(host.textContent).toContain('https://hooks.example.com/x');
    });

    it('reads an empty event list as every event, not as none', () => {
      expect(render().host.textContent).toContain('Every event');
    });

    it('lists the events a subscription asked for', () => {
      const { host } = render({
        subscriptions: [
          subscription({ events: ['BREAKING_CHANGE_SUBMITTED', 'VERSION_PROMOTED'] }),
        ],
      });

      expect(host.textContent).toContain('BREAKING_CHANGE_SUBMITTED, VERSION_PROMOTED');
      expect(host.textContent).not.toContain('Every event');
    });

    it('shows an event it has never heard of, as received', () => {
      expect(
        render({ subscriptions: [subscription({ events: ['TENANT_SUSPENDED'] })] }).host
          .textContent,
      ).toContain('TENANT_SUSPENDED');
    });

    it('marks a muted subscription', () => {
      expect(
        render({ subscriptions: [subscription({ enabled: false })] }).host.textContent,
      ).toContain('MUTED');
    });

    it('says what no subscriptions means', () => {
      const { host } = render({ subscriptions: [], isEmpty: true });

      expect(host.textContent).toContain('No subscriptions');
      expect(host.textContent).toContain('Nobody is told');
    });

    it('renders a failed load with a retry', () => {
      const { host } = render({
        error: new ConcordatError({ status: 404, code: 'x', detail: 'No such environment.' }),
      });

      expect(host.textContent).toContain('Could not load notifications');
    });
  });

  describe('the parked count', () => {
    it('is surfaced above the list, because nothing else says a subscriber is failing', () => {
      const { host } = render({ outbox: { pending: 0, parked: 3 } });

      expect(host.textContent).toContain('3 notifications were given up on');
    });

    it('is singular for one', () => {
      expect(render({ outbox: { pending: 0, parked: 1 } }).host.textContent).toContain(
        '1 notification was given up on',
      );
    });

    it('says nothing when nothing is parked', () => {
      expect(render({ outbox: { pending: 5, parked: 0 } }).host.textContent).not.toContain(
        'given up on',
      );
    });

    it('says nothing when the depth could not be read, rather than guessing', () => {
      expect(render({ outbox: null }).host.textContent).not.toContain('given up on');
    });
  });

  describe('who may change them', () => {
    it('offers every control to someone with env:write', () => {
      const { host } = render();

      expect(button(host, 'Mute')).toBeDefined();
      expect(button(host, 'Delete')).toBeDefined();
      expect(host.querySelector('#sub-endpoint')).not.toBeNull();
    });

    it('omits every one for a reader (ADR-018)', () => {
      const { host } = render({}, ['subject:read', 'env:read']);

      expect(button(host, 'Mute')).toBeUndefined();
      expect(button(host, 'Delete')).toBeUndefined();
      expect(host.querySelector('#sub-endpoint')).toBeNull();
      expect(host.textContent).toContain('https://hooks.example.com/x');
    });
  });

  describe('subscribing', () => {
    it('says that ticking nothing means every event, including any added later', () => {
      expect(render().host.textContent).toContain('None ticked means every event');
    });

    it('sends an empty event list when none is ticked', () => {
      const { fixture, host, create } = render();

      fill(host, 'sub-endpoint', 'https://hooks.example.com/new');
      fixture.detectChanges();
      host.querySelector('form')!.dispatchEvent(new Event('submit'));

      expect(create).toHaveBeenCalledWith({
        channel: 'EMAIL',
        endpoint: 'https://hooks.example.com/new',
        events: [],
      });
    });

    it('sends the ticked events in the registry’s order, and counts them', () => {
      const { fixture, host, create } = render();

      fill(host, 'sub-channel', 'WEBHOOK');
      fill(host, 'sub-endpoint', ' https://hooks.example.com/new ');
      tick(host, 'VERSION_PROMOTED');
      tick(host, 'BREAKING_CHANGE_SUBMITTED');
      fixture.detectChanges();

      expect(host.textContent).toContain('2 of 8 events');

      host.querySelector('form')!.dispatchEvent(new Event('submit'));

      expect(create).toHaveBeenCalledWith({
        channel: 'WEBHOOK',
        endpoint: 'https://hooks.example.com/new',
        events: ['BREAKING_CHANGE_SUBMITTED', 'VERSION_PROMOTED'],
      });
    });

    it('offers a policy change as an event of its own, with what it means', () => {
      // ADR-034. The form is driven by `NOTIFICATION_EVENTS`, so the box is there only if the web
      // vocabulary was taught the registry's new one (WireTokenTests pins the registry's side).
      const { host } = render();
      const labels = [...host.querySelectorAll('fieldset label')];
      const policy = labels.find(
        (l) => l.querySelector('.font-mono')?.textContent?.trim() === 'SUBJECT_POLICY_CHANGED',
      );

      expect(labels).toHaveLength(8);
      expect(policy?.textContent).toContain('compatibility policy');
    });

    it('sends a ticked policy change like any other event, alone', () => {
      const { fixture, host, create } = render();

      fill(host, 'sub-endpoint', 'a@b.c');
      tick(host, 'SUBJECT_POLICY_CHANGED');
      fixture.detectChanges();
      host.querySelector('form')!.dispatchEvent(new Event('submit'));

      expect(create).toHaveBeenCalledWith({
        channel: 'EMAIL',
        endpoint: 'a@b.c',
        events: ['SUBJECT_POLICY_CHANGED'],
      });
    });

    it('relabels the endpoint field for the channel, and says webhooks are https only', () => {
      const { fixture, host } = render();

      expect(host.querySelector('label[for="sub-endpoint"]')?.textContent).toContain(
        'Email address',
      );

      fill(host, 'sub-channel', 'WEBHOOK');
      fixture.detectChanges();

      expect(host.querySelector('label[for="sub-endpoint"]')?.textContent).toContain('Webhook URL');
      expect(host.textContent).toContain('https');
    });

    it('sends nothing for a blank endpoint', () => {
      const { fixture, host, create } = render();

      fill(host, 'sub-endpoint', '   ');
      fixture.detectChanges();
      host.querySelector('form')!.dispatchEvent(new Event('submit'));

      expect(create).not.toHaveBeenCalled();
    });

    it('empties the form once the subscription is created', () => {
      const { fixture, host, created } = render();

      fill(host, 'sub-endpoint', 'a@b.c');
      tick(host, 'VERSION_PROMOTED');
      fixture.detectChanges();
      created.set(subscription({ id: 's2' }));
      fixture.detectChanges();

      expect((host.querySelector('#sub-endpoint') as HTMLInputElement).value).toBe('');
      expect(host.textContent).toContain('None ticked means every event');
    });

    it('shows the registry’s refusal, such as a webhook that is not https', () => {
      const { host } = render({
        createError: new ConcordatError({
          status: 400,
          code: 'x',
          detail: 'Webhooks must be https.',
        }),
      });

      expect(host.textContent).toContain('Could not subscribe');
      expect(host.textContent).toContain('Webhooks must be https.');
    });
  });

  describe('the signing secret', () => {
    const secret = { endpoint: 'https://hooks.example.com/new', secret: 'whsec_SECRET' };

    it('is shown once, with the endpoint it signs for and what the receiver does with it', () => {
      const { host } = render({ signingSecret: secret });

      expect((host.querySelector('input[readonly]') as HTMLInputElement).value).toBe(
        'whsec_SECRET',
      );
      expect(host.textContent).toContain('will not be shown again');
      expect(host.textContent).toContain('https://hooks.example.com/new');
      expect(host.textContent).toContain('X-Concordat-Signature');
    });

    it('is cleared from the store when dismissed', () => {
      const { host, dismiss } = render({ signingSecret: secret });

      button(host, 'I have saved it')!.click();

      expect(dismiss).toHaveBeenCalledTimes(1);
    });

    it('is not on the page when there is none', () => {
      expect(render().host.querySelector('input[readonly]')).toBeNull();
    });
  });

  describe('muting and deleting', () => {
    it('mutes an enabled subscription', () => {
      const { host, setEnabled } = render();

      button(host, 'Mute')!.click();

      expect(setEnabled).toHaveBeenCalledWith({ id: 's1', enabled: false });
    });

    it('unmutes a muted one', () => {
      const { host, setEnabled } = render({ subscriptions: [subscription({ enabled: false })] });

      button(host, 'Unmute')!.click();

      expect(setEnabled).toHaveBeenCalledWith({ id: 's1', enabled: true });
    });

    it('disables the controls while a change to that subscription is in flight', () => {
      const { host } = render({ busy: ['s1'] });

      expect(button(host, 'Mute')!.disabled).toBe(true);
    });

    it('takes two clicks to delete, and says mute keeps the settings', () => {
      const { fixture, host, remove } = render();

      button(host, 'Delete')!.click();
      fixture.detectChanges();

      expect(remove).not.toHaveBeenCalled();
      expect(host.textContent).toContain('mute it to keep them');

      button(host, 'Confirm')!.click();

      expect(remove).toHaveBeenCalledWith('s1');
    });

    it('sends nothing when the delete is cancelled', () => {
      const { fixture, host, remove } = render();

      button(host, 'Delete')!.click();
      fixture.detectChanges();
      button(host, 'Cancel')!.click();
      fixture.detectChanges();

      expect(remove).not.toHaveBeenCalled();
    });

    it('shows a refusal on the subscription it concerns', () => {
      const { host } = render({
        rowErrors: { s1: new ConcordatError({ status: 404, code: 'x', detail: 'Already gone.' }) },
      });

      expect(host.textContent).toContain('Could not change the subscription');
      expect(host.textContent).toContain('Already gone.');
    });
  });

  describe('changing a subscription’s events', () => {
    const editor = (host: HTMLElement) =>
      host.querySelector<HTMLElement>('fieldset[aria-label^="Events for"]');

    function tickIn(host: HTMLElement, event: string): void {
      const box = [...editor(host)!.querySelectorAll('label')]
        .find((l) => l.querySelector('.font-mono')?.textContent?.trim() === event)
        ?.querySelector('input') as HTMLInputElement;
      box.click();
    }

    const ticked = (host: HTMLElement) =>
      [...editor(host)!.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')]
        .filter((i) => i.checked)
        .map((i) => i.closest('label')?.querySelector('.font-mono')?.textContent?.trim());

    it('is offered with env:write, and absent without it, not disabled', () => {
      expect(button(render().host, 'Change events')).toBeDefined();
    });

    it('renders nothing in its place for a reader', () => {
      const { host } = render({}, ['env:read']);

      expect(button(host, 'Change events')).toBeUndefined();
      expect(host.textContent).not.toContain('Change events');
    });

    it('is closed until asked for', () => {
      const { host } = render();

      expect(editor(host)).toBeNull();
      expect(button(host, 'Change events')!.getAttribute('aria-expanded')).toBe('false');
    });

    it('opens on the events the subscription holds now', () => {
      const { fixture, host } = render({
        subscriptions: [subscription({ events: ['VERSION_PROMOTED', 'SUBJECT_DEPRECATED'] })],
      });

      button(host, 'Change events')!.click();
      fixture.detectChanges();

      expect(ticked(host).sort()).toEqual(['SUBJECT_DEPRECATED', 'VERSION_PROMOTED']);
      expect(button(host, 'Change events')!.getAttribute('aria-expanded')).toBe('true');
    });

    it('opens with nothing ticked for a subscription that hears every event, and says what that means', () => {
      // Ticking all of them would be a different subscription: one that stops hearing the next
      // event the registry adds.
      const { fixture, host } = render({ subscriptions: [subscription({ events: [] })] });

      button(host, 'Change events')!.click();
      fixture.detectChanges();

      expect(ticked(host)).toEqual([]);
      expect(editor(host)!.textContent).toContain(
        'None ticked means every event, including any added later.',
      );
    });

    it('says it keeps the address and the signing secret', () => {
      const { fixture, host } = render();

      button(host, 'Change events')!.click();
      fixture.detectChanges();

      expect(editor(host)!.textContent).toContain(
        'Keeps this subscription, its address and its signing secret',
      );
    });

    it('saves the ticked events in the registry’s own order, and closes', () => {
      const { fixture, host, setEvents } = render();

      button(host, 'Change events')!.click();
      fixture.detectChanges();
      // Ticked in the reverse of the registry's order.
      tickIn(host, 'SUBJECT_DEPRECATED');
      tickIn(host, 'VERSION_PROMOTED');
      fixture.detectChanges();
      button(host, 'Save events')!.click();
      fixture.detectChanges();

      expect(setEvents).toHaveBeenCalledTimes(1);
      expect(setEvents).toHaveBeenCalledWith({
        id: 's1',
        events: ['VERSION_PROMOTED', 'SUBJECT_DEPRECATED'],
      });
      expect(editor(host)).toBeNull();
    });

    it('saves an empty list when everything is unticked, which is every event', () => {
      const { fixture, host, setEvents } = render({
        subscriptions: [subscription({ events: ['VERSION_PROMOTED'] })],
      });

      button(host, 'Change events')!.click();
      fixture.detectChanges();
      tickIn(host, 'VERSION_PROMOTED');
      fixture.detectChanges();
      button(host, 'Save events')!.click();

      expect(setEvents).toHaveBeenCalledWith({ id: 's1', events: [] });
    });

    it('sends nothing when cancelled', () => {
      const { fixture, host, setEvents } = render();

      button(host, 'Change events')!.click();
      fixture.detectChanges();
      tickIn(host, 'VERSION_PROMOTED');
      button(host, 'Cancel')!.click();
      fixture.detectChanges();

      expect(setEvents).not.toHaveBeenCalled();
      expect(editor(host)).toBeNull();
    });

    it('closes when the same button is pressed again', () => {
      const { fixture, host } = render();

      button(host, 'Change events')!.click();
      fixture.detectChanges();
      button(host, 'Change events')!.click();
      fixture.detectChanges();

      expect(editor(host)).toBeNull();
    });

    it('edits one subscription at a time, each on its own events', () => {
      const { fixture, host } = render({
        subscriptions: [
          subscription({ id: 's1', events: ['VERSION_PROMOTED'] }),
          subscription({ id: 's2', endpoint: 'https://hooks.example.com/y', events: [] }),
        ],
      });
      const buttons = () =>
        [...host.querySelectorAll('button')].filter(
          (b) => b.textContent?.trim() === 'Change events',
        );

      buttons()[0]!.click();
      fixture.detectChanges();
      buttons()[1]!.click();
      fixture.detectChanges();

      expect(host.querySelectorAll('fieldset[aria-label^="Events for"]')).toHaveLength(1);
      expect(editor(host)!.getAttribute('aria-label')).toBe(
        'Events for https://hooks.example.com/y',
      );
      expect(ticked(host)).toEqual([]);
    });

    it('is disabled while a change to that subscription is in flight', () => {
      const { host } = render({ busy: ['s1'] });

      expect(button(host, 'Change events')!.disabled).toBe(true);
    });

    it('shows the registry’s refusal on the row', () => {
      const { host } = render({
        rowErrors: {
          s1: new ConcordatError({
            status: 400,
            code: 'subscription_invalid',
            detail: 'Unknown event.',
          }),
        },
      });

      expect(host.textContent).toContain('Could not change the subscription');
      expect(host.textContent).toContain('Unknown event.');
    });
  });
});
