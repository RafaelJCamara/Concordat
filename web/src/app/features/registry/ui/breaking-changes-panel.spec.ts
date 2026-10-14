import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';
import type { HeldVersion } from '../../../domain/registry/environment-summary';
import { BreakingChangesPanel } from './breaking-changes-panel';

function held(overrides: Partial<HeldVersion> = {}): HeldVersion {
  return {
    subject: 'orders.created',
    ordinal: 3,
    status: 'AWAITING_APPROVAL',
    semanticVersion: null,
    registeredAt: new Date('2026-10-14T09:00:00.000Z'),
    registeredBy: 'ci',
    decidedAt: null,
    decidedBy: null,
    ...overrides,
  };
}

describe('BreakingChangesPanel', () => {
  let fixture: ComponentFixture<BreakingChangesPanel>;

  function render(changes: readonly HeldVersion[]): HTMLElement {
    fixture.componentRef.setInput('changes', changes);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [BreakingChangesPanel],
      providers: [provideRouter([])],
    });
    fixture = TestBed.createComponent(BreakingChangesPanel);
  });

  it('says what the panel lists, so "breaking" is not left to interpretation', () => {
    const host = render([held()]);

    expect(host.textContent).toContain('Held at the approval gate, or rejected.');
  });

  it('links each row to the version it names', () => {
    const host = render([held({ subject: 'orders.created', ordinal: 3 })]);

    expect(host.querySelector('a[href="/subjects/orders.created/versions/3"]')).not.toBeNull();
  });

  it('points at the approvals queue, where a held version is decided', () => {
    expect(render([held()]).querySelector('a[href="/approvals"]')).not.toBeNull();
  });

  it('shows a held version as awaiting approval, and who proposed it', () => {
    const host = render([held({ registeredBy: 'carol' })]);

    expect(host.textContent).toContain('Awaiting approval');
    expect(host.textContent).toContain('Proposed by carol');
    expect(host.textContent).not.toContain('Rejected');
  });

  it('shows a rejected version as rejected, and who rejected it', () => {
    // Red is what this app paints REJECTED, and a held version is not that (a held change is
    // waiting for a decision, not refused): the two must be told apart by their words as well as
    // their tone.
    const host = render([
      held({
        status: 'REJECTED',
        decidedAt: new Date('2026-10-14T10:00:00.000Z'),
        decidedBy: 'bob',
      }),
    ]);

    expect(host.textContent).toContain('Rejected');
    expect(host.textContent).toContain('rejected by bob');
    expect(host.textContent).not.toContain('Awaiting approval');
  });

  it('shows the label a version carried, when it carried one', () => {
    expect(render([held({ semanticVersion: '2.0.0' })]).textContent).toContain('2.0.0');
    expect(render([held({ semanticVersion: null })]).textContent).not.toContain('·  ·');
  });

  it('puts the exact times in titles beside the relative ones', () => {
    const host = render([
      held({
        status: 'REJECTED',
        decidedAt: new Date('2026-10-14T10:00:00.000Z'),
        decidedBy: 'bob',
      }),
    ]);

    const titles = [...host.querySelectorAll('[title]')].map((e) => e.getAttribute('title'));

    expect(titles).toContain('2026-10-14T09:00:00.000Z');
    expect(titles).toContain('2026-10-14T10:00:00.000Z');
  });

  it('keeps the order it was given', () => {
    const host = render([
      held({ subject: 'b.newer', ordinal: 2 }),
      held({ subject: 'a.older', ordinal: 5 }),
    ]);

    const names = [...host.querySelectorAll('li cd-subject-name')].map((e) =>
      e.textContent?.trim(),
    );

    expect(names).toEqual(['b.newer', 'a.older']);
  });

  describe('when nothing is held or rejected', () => {
    it('says so, and what would put something here', () => {
      const host = render([]);

      expect(host.textContent).toContain('Nothing held or rejected');
      expect(host.textContent).toContain('breaks a subject');
      expect(host.querySelector('li')).toBeNull();
    });

    it('says an approved change is not listed, so an empty panel is not read as "none ever"', () => {
      expect(render([]).textContent).toContain('Approved changes are live and are not listed');
    });
  });
});
