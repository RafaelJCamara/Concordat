import { signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { provideConcordatConfig } from '../../../core/config/app-config';
import { ConcordatError } from '../../../core/http/problem-details';
import type { ConsumerImpact, ImpactReport } from '../../../domain/registry/impact';
import { ImpactStore } from '../application/impact-store';
import { ImpactAnalysisPage } from './impact-analysis-page';

// The screen's own decisions: that an empty list is not rendered as "safe", and that the
// headline never folds a consumer the registry could not judge into either side of the count.

function consumer(overrides: Partial<ConsumerImpact> = {}): ConsumerImpact {
  return {
    service: 'billing',
    selector: '1',
    readerOrdinal: 1,
    breaks: true,
    certainty: 'CHECKED',
    lastSeenAt: new Date('2026-08-13T09:00:00Z'),
    stale: false,
    reasons: ['total is now required'],
    ...overrides,
  };
}

function reportOf(overrides: Partial<ImpactReport> = {}): ImpactReport {
  const consumers = overrides.consumers ?? [consumer()];

  return {
    subject: 'orders.created',
    candidateOrdinal: 2,
    candidateSchemaId: 'abc',
    breakingCount: consumers.filter((c) => c.breaks && c.certainty === 'CHECKED').length,
    consumers,
    contracts: [],
    ...overrides,
  };
}

function storeIn(state: {
  report?: ImpactReport | null;
  loading?: boolean;
  error?: ConcordatError | null;
}) {
  return {
    report: signal(state.report ?? null),
    loading: signal(state.loading ?? false),
    error: signal(state.error ?? null),
    load: () => {},
  } as unknown as InstanceType<typeof ImpactStore>;
}

function render(state: Parameters<typeof storeIn>[0]): {
  fixture: ComponentFixture<ImpactAnalysisPage>;
  host: HTMLElement;
} {
  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideConcordatConfig({ defaultEnvironment: 'dev' })],
  });

  TestBed.overrideComponent(ImpactAnalysisPage, {
    set: { providers: [{ provide: ImpactStore, useValue: storeIn(state) }] },
  });

  const fixture = TestBed.createComponent(ImpactAnalysisPage);
  fixture.componentRef.setInput('name', 'orders.created');
  fixture.componentRef.setInput('ordinal', 'latest');
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement };
}

describe('ImpactAnalysisPage', () => {
  it('links back to the subject', () => {
    const { host } = render({ report: reportOf() });

    expect(host.querySelector('a[href="/subjects/orders.created"]')).not.toBeNull();
  });

  it('names the version the registry resolved, not the word in the address', () => {
    // The route said `latest`; the heading must say which version that meant today.
    const { host } = render({ report: reportOf({ candidateOrdinal: 2 }) });

    expect(host.querySelector('h1')?.textContent).toContain('v2');
    expect(host.querySelector('h1 a[href="/subjects/orders.created/versions/2"]')).not.toBeNull();
  });

  it('counts the consumers that break', () => {
    const { host } = render({
      report: reportOf({
        consumers: [consumer(), consumer({ service: 'ledger', breaks: false })],
      }),
    });

    expect(host.textContent).toContain('1 breaking');
    expect(host.textContent).toContain('1 of 2 registered consumers break.');
  });

  it('agrees the verb with a single consumer', () => {
    const { host } = render({ report: reportOf() });

    expect(host.textContent).toContain('1 of 1 registered consumer breaks.');
  });

  it('does not fold a consumer it could not judge into the count', () => {
    // "0 of 2 break" would be true and misleading at once: one of the two was never judged.
    const { host } = render({
      report: reportOf({
        consumers: [
          consumer({ breaks: false }),
          consumer({
            service: 'dash',
            selector: 'latest',
            readerOrdinal: null,
            breaks: false,
            certainty: 'FOLLOWS_LATEST',
            reasons: [],
          }),
        ],
      }),
    });

    expect(host.textContent).toContain('0 of 2 registered consumers break.');
    expect(host.textContent).toContain('1 could not be judged');
    expect(host.textContent).toContain('No checked consumer breaks');
  });

  it('does not say "nothing breaks" when nothing was checked', () => {
    const { host } = render({
      report: reportOf({
        consumers: [
          consumer({
            selector: 'latest',
            readerOrdinal: null,
            breaks: false,
            certainty: 'FOLLOWS_LATEST',
            reasons: [],
          }),
        ],
      }),
    });

    expect(host.textContent).not.toContain('No checked consumer breaks');
  });

  it('says an empty list is a statement about declarations, not about safety', () => {
    const { host } = render({ report: reportOf({ consumers: [] }) });

    expect(host.textContent).toContain('No registered consumers');
    expect(host.textContent).toContain('not a finding that nobody is affected');
    expect(host.textContent).not.toContain('No checked consumer breaks');
  });

  it('lists the contracts that carry the subject', () => {
    const { host } = render({ report: reportOf({ contracts: ['payments'] }) });

    expect(host.textContent).toContain('Contracts that carry this subject');
    expect(host.textContent).toContain('payments');
  });

  it('shows a skeleton and no verdict while loading', () => {
    const { host } = render({ loading: true });

    expect(host.querySelector('[aria-busy="true"]')).not.toBeNull();
    expect(host.querySelector('h1')).toBeNull();
  });

  it('renders the registry refusal when there is no report', () => {
    const { host } = render({
      error: new ConcordatError({
        status: 404,
        code: 'version_not_found',
        detail: 'Subject has no version 9.',
      }),
    });

    expect(host.textContent).toContain('Could not analyse this version');
    expect(host.textContent).toContain('Subject has no version 9.');
  });
});
