import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { Divergence } from '../../../domain/registry/compatibility';
import { DivergenceList } from './divergence-list';

// Shared by registration, the diff screen and the approval queue. The property worth pinning
// is that an unknown `kind` renders unchanged: the catalogue grows server-side per format, and
// a component that switched on it would need a release every time the registry learned one.

function divergence(overrides: Partial<Divergence> = {}): Divergence {
  return {
    path: '/properties/discount',
    kind: 'required_field_added',
    direction: 'BACKWARD',
    surface: 'WIRE_JSON',
    message: 'A required field was added, so existing producers would fail validation.',
    conflictsWithVersion: 1,
    ...overrides,
  };
}

describe('DivergenceList', () => {
  let fixture: ComponentFixture<DivergenceList>;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [DivergenceList] });
    fixture = TestBed.createComponent(DivergenceList);
  });

  function render(divergences: readonly Divergence[], heading?: string): HTMLElement {
    fixture.componentRef.setInput('divergences', divergences);

    if (heading !== undefined) {
      fixture.componentRef.setInput('heading', heading);
    }

    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  it('leads with the JSON Pointer, which is the actionable part', () => {
    // "A required field was added" is not actionable; `/properties/discount` is. The path is
    // what someone takes back to their schema file.
    const host = render([divergence()]);

    expect(host.querySelector('code')?.textContent).toBe('/properties/discount');
  });

  it('renders the registry’s message verbatim', () => {
    const host = render([divergence()]);

    expect(host.textContent).toContain(
      'A required field was added, so existing producers would fail validation.',
    );
  });

  it('renders a kind it has never seen, rather than dropping the row', () => {
    // Avro and Protobuf add their own kinds. A UI that only rendered known ones would
    // silently hide the finding that mattered on the format it had not been updated for.
    const host = render([
      divergence({ kind: 'enum_symbol_removed', message: 'A symbol was removed.' }),
    ]);

    expect(host.textContent).toContain('enum_symbol_removed');
    expect(host.textContent).toContain('A symbol was removed.');
  });

  it('groups by the version each difference conflicts with', () => {
    // A transitive mode compares against several versions at once. A flat list repeats the
    // same path three times with nothing saying they are three separate comparisons.
    const host = render([
      divergence({ conflictsWithVersion: 1 }),
      divergence({ conflictsWithVersion: 2, path: '/properties/total' }),
    ]);

    expect(host.textContent).toContain('v1');
    expect(host.textContent).toContain('v2');
    expect(host.querySelectorAll('ul')).toHaveLength(2);
  });

  it('puts the nearest version first', () => {
    // Under a transitive mode the nearest is the conflict a reader can usually act on.
    const host = render([
      divergence({ conflictsWithVersion: 1 }),
      divergence({ conflictsWithVersion: 3 }),
    ]);

    // `div > p` is the group label; a message paragraph is a child of its `li`.
    const groups = [...host.querySelectorAll('div > p')].map((p) => p.textContent ?? '');

    expect(groups[0]).toContain('v3');
    expect(groups[1]).toContain('v1');
  });

  it('keeps two findings on one path apart when they are different kinds', () => {
    const host = render([
      divergence({ kind: 'required_field_added' }),
      divergence({ kind: 'type_narrowed' }),
    ]);

    expect(host.querySelectorAll('li')).toHaveLength(2);
  });

  it('names the axis and surface, because a mode change turns one into the other', () => {
    const host = render([divergence({ direction: 'FORWARD', surface: 'SOURCE' })]);

    expect(host.textContent).toContain('FORWARD');
    expect(host.textContent).toContain('SOURCE');
  });

  it('takes a heading, because the same data means different things per screen', () => {
    const host = render([divergence()], 'Why this was held');

    expect(host.querySelector('h2')?.textContent?.trim()).toBe('Why this was held');
  });

  it('renders nothing but the heading when there is nothing to report', () => {
    const host = render([]);

    expect(host.querySelectorAll('li')).toHaveLength(0);
  });
});
