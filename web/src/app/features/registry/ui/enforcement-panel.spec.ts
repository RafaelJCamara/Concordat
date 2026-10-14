import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';
import type {
  EnforcementTier,
  EnvironmentSummary,
} from '../../../domain/registry/environment-summary';
import { EnforcementPanel } from './enforcement-panel';

function summary(enforcement: readonly EnforcementTier[]): EnvironmentSummary {
  return {
    subjects: { total: 0, active: 0, deprecated: 0, retired: 0 },
    versions: { total: 0, active: 0, awaitingApproval: 0, rejected: 0, dismissed: 0 },
    newestSubjects: [],
    breakingChanges: [],
    enforcement,
    violations: { distinct: 0, recent: 0, windowHours: 24 },
  };
}

const TIERS: readonly EnforcementTier[] = [
  { mode: 'OFF', contracts: 1, publishBindings: 1, consumeBindings: 0 },
  { mode: 'MONITOR', contracts: 2, publishBindings: 3, consumeBindings: 1 },
  { mode: 'ENFORCE', contracts: 0, publishBindings: 0, consumeBindings: 0 },
];

describe('EnforcementPanel', () => {
  let fixture: ComponentFixture<EnforcementPanel>;

  function render(value: EnvironmentSummary): HTMLElement {
    fixture.componentRef.setInput('summary', value);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const rows = (host: HTMLElement) => [...host.querySelectorAll('li')].map((li) => li.textContent);

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [EnforcementPanel], providers: [provideRouter([])] });
    fixture = TestBed.createComponent(EnforcementPanel);
  });

  it('shows one row per mode, in the order the registry gave', () => {
    const host = render(summary(TIERS));

    const modes = rows(host).map((text) => /OFF|MONITOR|ENFORCE/.exec(text ?? '')?.[0]);

    expect(modes).toEqual(['OFF', 'MONITOR', 'ENFORCE']);
  });

  it('shows a mode nothing is set to as zero, not as a missing row', () => {
    // A row that vanished when empty would read "no contracts are enforcing" as "the panel does not
    // know about ENFORCE".
    const host = render(summary(TIERS));

    expect(rows(host)[2]).toContain('0 contracts');
    expect(rows(host)[2]).toContain('0 publish bindings');
  });

  it('counts contracts and both kinds of binding on each row', () => {
    const host = render(summary(TIERS));

    expect(rows(host)[1]).toContain('2 contracts');
    expect(rows(host)[1]).toContain('3 publish bindings');
    expect(rows(host)[1]).toContain('1 consume binding');
    expect(rows(host)[1]).not.toContain('1 consume bindings');
  });

  it('counts a single contract in the singular', () => {
    expect(rows(render(summary(TIERS)))[0]).toContain('1 contract');
    expect(rows(render(summary(TIERS)))[0]).not.toContain('1 contracts');
  });

  it('says what the numbers are not: declared, not observed, and not a share of anything', () => {
    const host = render(summary(TIERS));

    expect(host.textContent).toContain('Declared, not observed');
    expect(host.textContent).toContain('never sees message traffic');
    expect(host.textContent).not.toContain('%');
  });

  it('says that no violation reports is not the same as no violations', () => {
    expect(render(summary(TIERS)).textContent).toContain(
      'none reported is not the same as none happened',
    );
  });

  it('links to the contracts', () => {
    expect(render(summary(TIERS)).querySelector('a[href="/contracts"]')).not.toBeNull();
  });

  describe('with no contracts at all', () => {
    const none = summary(TIERS.map((t) => ({ ...t, contracts: 0, publishBindings: 0 })));

    it('says nothing is governed, instead of three rows of zeros', () => {
      const host = render(none);

      expect(host.textContent).toContain('No contracts in this environment');
      expect(host.querySelector('li')).toBeNull();
    });

    it('still carries the caveat, which is true of an empty panel too', () => {
      expect(render(none).textContent).toContain('Declared, not observed');
    });
  });

  it('does not call an environment bare when its only contracts are switched off', () => {
    const host = render(
      summary([
        { mode: 'OFF', contracts: 2, publishBindings: 0, consumeBindings: 0 },
        { mode: 'MONITOR', contracts: 0, publishBindings: 0, consumeBindings: 0 },
        { mode: 'ENFORCE', contracts: 0, publishBindings: 0, consumeBindings: 0 },
      ]),
    );

    expect(host.textContent).not.toContain('No contracts in this environment');
    expect(rows(host)[0]).toContain('2 contracts');
  });
});
