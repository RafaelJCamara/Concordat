import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { ConsumerImpact } from '../../../domain/registry/impact';
import { ConsumerImpactList } from './consumer-impact-list';

// The property worth pinning is that the verdict is read from `certainty` before `breaks`. A
// consumer that follows `latest` carries `breaks: false`, and badging it as compatible would
// turn "the registry has no answer" into "safe to approve".

function consumer(overrides: Partial<ConsumerImpact> = {}): ConsumerImpact {
  return {
    service: 'billing',
    selector: '1',
    readerOrdinal: 1,
    breaks: false,
    certainty: 'CHECKED',
    lastSeenAt: new Date('2026-08-13T09:00:00Z'),
    stale: false,
    reasons: [],
    ...overrides,
  };
}

describe('ConsumerImpactList', () => {
  let fixture: ComponentFixture<ConsumerImpactList>;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [ConsumerImpactList] });
    fixture = TestBed.createComponent(ConsumerImpactList);
  });

  function render(consumers: readonly ConsumerImpact[]): HTMLElement {
    fixture.componentRef.setInput('consumers', consumers);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  it('says BREAKS for a checked consumer that breaks, with the engine’s reasons', () => {
    const host = render([consumer({ breaks: true, reasons: ['total is now required'] })]);

    expect(host.textContent).toContain('BREAKS');
    expect(host.textContent).toContain('total is now required');
  });

  it('says COMPATIBLE for a checked consumer that does not', () => {
    expect(render([consumer()]).textContent).toContain('COMPATIBLE');
  });

  it('does not call a consumer that follows latest compatible', () => {
    const host = render([
      consumer({ selector: 'latest', readerOrdinal: null, certainty: 'FOLLOWS_LATEST' }),
    ]);

    expect(host.textContent).toContain('FOLLOWS LATEST');
    expect(host.textContent).not.toContain('COMPATIBLE');
    expect(host.textContent).toContain('cannot say whether this consumer breaks');
  });

  it('says why an unresolvable consumer cannot be judged', () => {
    const host = render([consumer({ certainty: 'UNRESOLVABLE' })]);

    expect(host.textContent).toContain('UNRESOLVABLE');
    expect(host.textContent).toContain('no longer exists');
    expect(host.textContent).not.toContain('COMPATIBLE');
  });

  it('states what the consumer declared and the version it was judged as', () => {
    const host = render([consumer({ selector: '>=1', readerOrdinal: 1 })]);

    expect(host.textContent).toContain('declares >=1');
    expect(host.textContent).toContain('judged as v1');
  });

  it('marks a stale registration without dropping it', () => {
    const host = render([consumer({ stale: true })]);

    expect(host.textContent).toContain('STALE');
    expect(host.textContent).toContain('billing');
  });

  it('puts the exact time in a title, beside the relative one', () => {
    const host = render([consumer()]);

    expect(host.querySelector('[title="2026-08-13T09:00:00.000Z"]')).not.toBeNull();
  });
});
