import { signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { AuditEntry, AuditQuery } from '../../../domain/governance/audit';
import { AuditStore } from '../application/audit-store';
import { AuditLogPage } from './audit-log-page';

// The screen's own decisions: that a filter is applied by submitting and not by typing, that
// dates mean whole local days, and that a cut-off answer says so.

function entry(): AuditEntry {
  return {
    at: new Date('2026-08-13T09:00:00Z'),
    action: 'VERSION_APPROVED',
    actor: 'alice',
    target: 'orders.created',
    detail: 'version 2',
  };
}

function render(
  state: {
    entries?: readonly AuditEntry[];
    loading?: boolean;
    isEmpty?: boolean;
    truncated?: boolean;
    error?: ConcordatError | null;
  } = {},
): { fixture: ComponentFixture<AuditLogPage>; host: HTMLElement; load: ReturnType<typeof vi.fn> } {
  const load = vi.fn();

  TestBed.configureTestingModule({
    providers: [provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });

  TestBed.overrideComponent(AuditLogPage, {
    set: {
      providers: [
        {
          provide: AuditStore,
          useValue: {
            entries: signal(state.entries ?? []),
            loading: signal(state.loading ?? false),
            isEmpty: signal(state.isEmpty ?? false),
            truncated: signal(state.truncated ?? false),
            error: signal(state.error ?? null),
            load,
          },
        },
      ],
    },
  });

  const fixture = TestBed.createComponent(AuditLogPage);
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, load };
}

function field<T extends HTMLElement>(host: HTMLElement, id: string): T {
  return host.querySelector(`#${id}`) as T;
}

function type(element: HTMLInputElement | HTMLSelectElement, value: string): void {
  element.value = value;
  element.dispatchEvent(new Event(element instanceof HTMLSelectElement ? 'change' : 'input'));
}

function lastQuery(load: ReturnType<typeof vi.fn>): AuditQuery {
  return load.mock.calls.at(-1)![0] as AuditQuery;
}

describe('AuditLogPage', () => {
  it('loads once on arrival, scoped to this environment with nothing filtered', () => {
    const { load } = render();

    expect(load).toHaveBeenCalledTimes(1);
    expect(lastQuery(load)).toEqual({
      scope: 'environment',
      action: null,
      target: '',
      since: null,
      until: null,
      limit: 100,
    });
  });

  it('shows a row whose action this app does not know beside the ones it does, as received', () => {
    // ADR-036: a registry rolled back past a change that added an action now answers the row
    // instead of failing the read, and this page is where somebody finds out what it holds.
    const { host } = render({
      entries: [{ ...entry(), action: 'A_FUTURE_ACTION', detail: 'from a newer build' }, entry()],
    });

    const rows = [...host.querySelectorAll('tbody tr')].map((row) => row.textContent);

    expect(rows).toHaveLength(2);
    expect(rows[0]).toContain('A_FUTURE_ACTION');
    expect(rows[0]).toContain('from a newer build');
    expect(rows[1]).toContain('VERSION_APPROVED');
  });

  it('names the environment it is reading', () => {
    expect(render().host.querySelector('header')?.textContent).toContain('dev');
  });

  describe('filtering', () => {
    it('does not reload while typing, only when applied', () => {
      const { fixture, host, load } = render();

      type(field<HTMLInputElement>(host, 'audit-target'), 'orders.created');
      fixture.detectChanges();

      expect(load).toHaveBeenCalledTimes(1);

      host.querySelector('form')!.dispatchEvent(new Event('submit'));

      expect(load).toHaveBeenCalledTimes(2);
      expect(lastQuery(load).target).toBe('orders.created');
    });

    it('sends the chosen action, and null for "any"', () => {
      const { host, load } = render();

      type(field<HTMLSelectElement>(host, 'audit-action'), 'VERSION_REJECTED');
      host.querySelector('form')!.dispatchEvent(new Event('submit'));
      expect(lastQuery(load).action).toBe('VERSION_REJECTED');

      type(field<HTMLSelectElement>(host, 'audit-action'), '');
      host.querySelector('form')!.dispatchEvent(new Event('submit'));
      expect(lastQuery(load).action).toBeNull();
    });

    it('offers every action the registry can record', () => {
      const { host } = render();

      const options = [...field<HTMLSelectElement>(host, 'audit-action').options].map(
        (o) => o.value,
      );

      expect(options).toContain('VERSION_APPROVED');
      expect(options).toContain('API_KEY_REVOKED');
      expect(options).toContain('SUBJECT_POLICY_CHANGED');
      expect(options).toContain('CONTRACT_BINDING_CHANGED');
      expect(options).toContain('CONTRACT_BINDING_REMOVED');
      expect(options).toContain('SUBJECT_REVIEWERS_CHANGED');
      expect(options).toHaveLength(28); // 27 actions and "any"
    });

    it('widens to every environment, and says so in the heading', () => {
      const { fixture, host, load } = render();

      type(field<HTMLSelectElement>(host, 'audit-scope'), 'all');
      host.querySelector('form')!.dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      expect(lastQuery(load).scope).toBe('all');
      expect(host.querySelector('header')?.textContent).toContain('every environment');
    });

    it('treats a date as the whole local day: start for since, end for until', () => {
      // Not UTC midnight: a picker that quietly meant UTC would put entries on the wrong side
      // of a day for everyone who is not on it.
      const { host, load } = render();

      type(field<HTMLInputElement>(host, 'audit-since'), '2026-08-03');
      type(field<HTMLInputElement>(host, 'audit-until'), '2026-08-05');
      host.querySelector('form')!.dispatchEvent(new Event('submit'));

      const query = lastQuery(load);

      expect(query.since).toEqual(new Date('2026-08-03T00:00:00'));
      expect(query.until).toEqual(new Date('2026-08-05T23:59:59.999'));
    });

    it('sends the limit as a number', () => {
      const { host, load } = render();

      type(field<HTMLSelectElement>(host, 'audit-limit'), '500');
      host.querySelector('form')!.dispatchEvent(new Event('submit'));

      expect(lastQuery(load).limit).toBe(500);
    });

    it('resets to the opening query and reloads', () => {
      const { host, load } = render();

      type(field<HTMLInputElement>(host, 'audit-target'), 'orders.created');
      [...host.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Reset')!.click();

      expect(lastQuery(load).target).toBe('');
      expect(lastQuery(load).limit).toBe(100);
    });
  });

  describe('what it shows', () => {
    it('renders the entries', () => {
      const { host } = render({ entries: [entry()] });

      expect(host.querySelector('cd-audit-table')).not.toBeNull();
      expect(host.textContent).toContain('orders.created');
    });

    it('says when the answer was cut off', () => {
      const { host } = render({ entries: [entry()], truncated: true });

      expect(host.querySelector('[role="status"]')?.textContent).toContain('older entries');
    });

    it('says nothing about truncation for a complete answer', () => {
      const { host } = render({ entries: [entry()], truncated: false });

      expect(host.querySelector('[role="status"]')).toBeNull();
    });

    it('explains that an empty result is not evidence that nobody tried', () => {
      const { host } = render({ isEmpty: true });

      expect(host.textContent).toContain('No entries match');
      expect(host.textContent).toContain('not evidence that nobody tried it');
    });

    it('shows a skeleton while loading', () => {
      const { host } = render({ loading: true });

      expect(host.querySelector('[aria-busy="true"]')).not.toBeNull();
      expect(host.querySelector('cd-audit-table')).toBeNull();
    });

    it('renders the registry’s refusal, and retries with the same filter', () => {
      const { host, load } = render({
        error: new ConcordatError({
          status: 400,
          code: 'audit_filter_invalid',
          detail: 'Since is after until.',
        }),
      });

      expect(host.textContent).toContain('Could not load the audit log');
      expect(host.textContent).toContain('Since is after until.');

      [...host.querySelectorAll('button')]
        .find((b) => b.textContent?.trim() === 'Try again')!
        .click();

      expect(load).toHaveBeenCalledTimes(2);
    });
  });
});
