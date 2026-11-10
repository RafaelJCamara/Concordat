import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { Divergence } from '../../../domain/registry/compatibility';
import type { SchemaDiffReady } from '../../../domain/registry/schema-diff';
import { SchemaDiff, UNFOLD_CHUNK } from './schema-diff';

// What a reviewer and a screen reader are given: the marking of a change by more than colour, a
// fold that is a real button and opens, a finding on the line it is about, and a scrolling region
// that can be reached from the keyboard. The diff itself is `schema-diff.spec.ts` in `domain/`.

const finding = (path: string, message = 'm'): Divergence => ({
  path,
  kind: 'type_narrowed',
  direction: 'BACKWARD',
  surface: 'WIRE_JSON',
  message,
  conflictsWithVersion: 1,
});

const result: SchemaDiffReady = {
  status: 'ready',
  segments: [
    { kind: 'folded', oldStart: 1, newStart: 1, count: 12 },
    {
      kind: 'rows',
      rows: [
        { kind: 'context', oldNumber: 13, newNumber: 13, text: '  "id": {', findings: [0] },
        {
          kind: 'removed',
          oldNumber: 14,
          newNumber: null,
          text: '    "type": "string"',
          findings: [],
        },
        {
          kind: 'added',
          oldNumber: null,
          newNumber: 14,
          text: '    "type": "integer"',
          findings: [],
        },
      ],
    },
    { kind: 'folded', oldStart: 15, newStart: 15, count: 1 },
  ],
  lines: Array.from({ length: 15 }, (_, i) => `line ${i + 1}`),
  added: 1,
  removed: 1,
  unplaced: [1],
};

function render(
  overrides: Partial<{ result: SchemaDiffReady; divergences: readonly Divergence[] }> = {},
) {
  const fixture = TestBed.createComponent(SchemaDiff);
  fixture.componentRef.setInput('result', overrides.result ?? result);
  fixture.componentRef.setInput(
    'divergences',
    overrides.divergences ?? [
      finding('#/properties/id', 'id changed type'),
      finding('#/gone', 'gone is gone'),
    ],
  );
  fixture.componentRef.setInput('label', 'Schema changes, v1 to v2');
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement };
}

const rows = (host: HTMLElement, kind: string) => [
  ...host.querySelectorAll<HTMLElement>(`[data-row="${kind}"]`),
];

describe('SchemaDiff', () => {
  it('marks an added and a removed line with a sign and with words, not only a colour', () => {
    const { host } = render();
    const [removed] = rows(host, 'removed');
    const [added] = rows(host, 'added');

    // The sign column is for sighted readers, and hidden from the others...
    expect(removed?.querySelector('[aria-hidden="true"].font-bold')?.textContent).toBe('-');
    expect(added?.querySelector('[aria-hidden="true"].font-bold')?.textContent).toBe('+');
    // ...who are told in words, before the line.
    expect(removed?.querySelector('.sr-only')?.textContent).toBe('Removed: ');
    expect(added?.querySelector('.sr-only')?.textContent).toBe('Added: ');
    expect(removed?.querySelector('code')?.textContent).toBe('Removed:     "type": "string"');
  });

  it('says nothing extra before an unchanged line', () => {
    const { host } = render();
    const [context] = rows(host, 'context');

    expect(context?.querySelector('.sr-only')?.textContent).toBe('');
    expect(context?.textContent).not.toContain('Added');
  });

  it('shows each side’s own line number and hides the numbers from a screen reader', () => {
    const { host } = render();
    const [removed] = rows(host, 'removed');
    const [added] = rows(host, 'added');
    const numbers = (row?: HTMLElement) =>
      [...(row?.querySelectorAll('[aria-hidden="true"]') ?? [])].map((n) => n.textContent?.trim());

    expect(numbers(removed).slice(0, 2)).toEqual(['14', '']);
    expect(numbers(added).slice(0, 2)).toEqual(['', '14']);
  });

  it('totals what changed, in words', () => {
    const { host } = render();

    expect(host.querySelector('[data-testid="diff-totals"]')?.textContent).toMatch(
      /\+1\s+line added,\s+−1\s+removed/,
    );
    expect(host.querySelector('[data-testid="diff-totals"]')?.textContent).toContain(
      '1 finding is not tied to a line',
    );
  });

  it('folds unchanged lines behind a real button that says how many', () => {
    const { host } = render();
    const buttons = [...host.querySelectorAll('button')];

    expect(buttons.map((b) => b.getAttribute('type'))).toEqual(['button', 'button']);
    expect(buttons[0]?.textContent?.trim()).toContain('12 unchanged lines');
    expect(buttons[0]?.getAttribute('aria-label')).toBe('Show 12 unchanged lines');
    // The singular, because "1 unchanged lines" reads like a bug.
    expect(buttons[1]?.getAttribute('aria-label')).toBe('Show 1 unchanged line');
  });

  it('opens a fold to the lines it hid, numbered, and the button goes', () => {
    const { fixture, host } = render();

    host.querySelectorAll('button')[0]?.click();
    fixture.detectChanges();

    const context = rows(host, 'context');
    // Twelve revealed, plus the one context row that was already on show.
    expect(context).toHaveLength(13);
    expect(context[0]?.querySelector('code')?.textContent).toBe('line 1');
    expect(context[11]?.querySelector('code')?.textContent).toBe('line 12');
    expect(host.querySelectorAll('button')).toHaveLength(1);
  });

  it('opens one fold without opening the other', () => {
    const { fixture, host } = render();

    host.querySelectorAll('button')[1]?.click();
    fixture.detectChanges();

    expect(host.querySelectorAll('button')).toHaveLength(1);
    expect(host.querySelector('button')?.getAttribute('aria-label')).toBe(
      'Show 12 unchanged lines',
    );
    expect(rows(host, 'context')).toHaveLength(2);
  });

  it('opens a very long fold a chunk at a time, so one click cannot draw fifty thousand lines', () => {
    const total = UNFOLD_CHUNK * 2 + 200;
    const { fixture, host } = render({
      result: {
        ...result,
        segments: [{ kind: 'folded', oldStart: 1, newStart: 1, count: total }],
        lines: Array.from({ length: total }, (_, i) => `line ${i + 1}`),
        unplaced: [],
      },
    });
    const label = () => host.querySelector('button')?.getAttribute('aria-label');
    const click = () => {
      host.querySelector('button')?.click();
      fixture.detectChanges();
    };

    expect(label()).toBe(`Show ${total} unchanged lines`);

    click();

    expect(rows(host, 'context')).toHaveLength(UNFOLD_CHUNK);
    expect(label()).toBe(`Show ${total - UNFOLD_CHUNK} unchanged lines`);
    // The next chunk continues where the last stopped.
    click();
    const lines = rows(host, 'context').map((r) => r.querySelector('code')?.textContent);
    expect(lines).toHaveLength(UNFOLD_CHUNK * 2);
    expect(lines[UNFOLD_CHUNK]).toBe(`line ${UNFOLD_CHUNK + 1}`);
    expect(label()).toBe('Show 200 unchanged lines');

    click();

    expect(rows(host, 'context')).toHaveLength(total);
    expect(host.querySelector('button')).toBeNull();
  });

  it('puts a finding directly under the line it is about, with the registry’s own words', () => {
    const { host } = render();
    const note = host.querySelector('[data-testid="diff-finding"]');
    const row = rows(host, 'context')[0];

    expect(note?.previousElementSibling).toBe(row);
    expect(note?.getAttribute('role')).toBe('note');
    expect(note?.textContent).toContain('type_narrowed');
    expect(note?.textContent).toContain('BACKWARD · WIRE_JSON');
    expect(note?.textContent).toContain('id changed type');
    expect(note?.querySelector('.sr-only')?.textContent).toContain('finding on the line above');
  });

  it('shows a finding the registry reported once per direction as one, naming both', () => {
    const forward: Divergence = {
      ...finding('#/properties/id', 'id changed type'),
      direction: 'FORWARD',
    };
    const other = finding('#/properties/id', 'something else entirely');
    const { host } = render({
      divergences: [finding('#/properties/id', 'id changed type'), forward, other],
      result: {
        ...result,
        segments: [
          {
            kind: 'rows',
            rows: [{ kind: 'context', oldNumber: 1, newNumber: 1, text: 'x', findings: [0, 1, 2] }],
          },
        ],
        unplaced: [],
      },
    });
    const notes = [...host.querySelectorAll('[data-testid="diff-finding"]')];

    expect(notes).toHaveLength(2);
    expect(notes[0]?.textContent).toContain('BACKWARD + FORWARD');
    expect(notes[0]?.textContent).toContain('id changed type');
    expect(notes[1]?.textContent).toContain('something else entirely');
    expect(notes[1]?.textContent).not.toContain('+ FORWARD');
  });

  it('lists a finding with no line, with its path, instead of dropping it', () => {
    const { host } = render();
    const unplaced = host.querySelector('[data-testid="diff-unplaced"]');

    expect(unplaced?.textContent).toContain('Findings not tied to a line');
    expect(unplaced?.textContent).toContain('/gone');
    expect(unplaced?.textContent).toContain('gone is gone');
  });

  it('shows no such list when every finding has a line', () => {
    const { host } = render({ result: { ...result, unplaced: [] } });

    expect(host.querySelector('[data-testid="diff-unplaced"]')).toBeNull();
    expect(host.querySelector('[data-testid="diff-totals"]')?.textContent).not.toContain(
      'not tied',
    );
  });

  it('makes the scrolling region reachable from the keyboard, and names it', () => {
    const { host } = render();
    const region = host.querySelector('[data-testid="diff-region"]');

    expect(region?.getAttribute('role')).toBe('region');
    expect(region?.getAttribute('tabindex')).toBe('0');
    expect(region?.getAttribute('aria-label')).toBe('Schema changes, v1 to v2');
  });

  it('renders schema text as text, never as markup', () => {
    const hostile = '<img src=x onerror=alert(1)>';
    const { host } = render({
      result: {
        ...result,
        segments: [
          {
            kind: 'rows',
            rows: [{ kind: 'added', oldNumber: null, newNumber: 1, text: hostile, findings: [] }],
          },
        ],
        unplaced: [],
      },
    });

    expect(host.querySelector('img')).toBeNull();
    expect(host.querySelector('code')?.textContent).toContain(hostile);
  });

  it('says so when the documents differ in nothing a line shows', () => {
    const { host } = render({
      result: { ...result, segments: [], added: 0, removed: 0, unplaced: [] },
    });

    expect(host.textContent).toContain('No line differs once both documents are laid out');
    expect(host.querySelector('[data-testid="diff-region"]')).toBeNull();
  });

  it('starts with every fold closed again when a new diff arrives', () => {
    const { fixture, host } = render();

    host.querySelectorAll('button')[0]?.click();
    fixture.detectChanges();
    expect(host.querySelectorAll('button')).toHaveLength(1);

    fixture.componentRef.setInput('result', { ...result });
    fixture.detectChanges();

    expect(host.querySelectorAll('button')).toHaveLength(2);
  });
});
