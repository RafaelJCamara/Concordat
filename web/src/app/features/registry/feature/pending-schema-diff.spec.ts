import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import type { VersionDiff } from '../../../domain/registry/diff';
import type { SchemaDiffResult } from '../../../domain/registry/schema-diff';
import { SchemaDiffStore, type SchemaDiffProblem } from '../application/schema-diff-store';
import { PendingSchemaDiff } from './pending-schema-diff';

// The component joins a store's answer to the page: it asks for the right pair, shows the diff,
// and for every way the diff cannot be shown says one sentence and tells the parent to show the
// summary instead. The sentences are pinned because they are the guard's whole user interface.

const versionDiff: VersionDiff = {
  from: 1,
  to: 2,
  fromSchemaId: 'schema-1',
  toSchemaId: 'schema-2',
  identical: false,
  policy: { mode: 'BACKWARD', surface: 'WIRE_JSON' },
  divergences: [
    {
      path: '#/required',
      kind: 'required_field_added',
      direction: 'BACKWARD',
      surface: 'WIRE_JSON',
      message: 'now required',
      conflictsWithVersion: 1,
    },
  ],
};

const ready: SchemaDiffResult = {
  status: 'ready',
  segments: [
    {
      kind: 'rows',
      rows: [{ kind: 'added', oldNumber: null, newNumber: 1, text: '"x": 1', findings: [0] }],
    },
  ],
  lines: [],
  added: 1,
  removed: 0,
  unplaced: [],
};

function render(
  state: {
    loading?: boolean;
    result?: SchemaDiffResult | null;
    problem?: SchemaDiffProblem | null;
    diff?: VersionDiff;
  } = {},
) {
  const load = vi.fn();
  const fellBack: boolean[] = [];

  TestBed.overrideComponent(PendingSchemaDiff, {
    set: {
      providers: [
        {
          provide: SchemaDiffStore,
          useValue: {
            loading: signal(state.loading ?? false),
            result: signal(state.result ?? null),
            problem: signal(state.problem ?? null),
            load,
          },
        },
      ],
    },
  });

  const fixture = TestBed.createComponent(PendingSchemaDiff);
  fixture.componentRef.setInput('diff', state.diff ?? versionDiff);
  fixture.componentInstance.fellBack.subscribe((value) => fellBack.push(value));
  fixture.detectChanges();

  return { host: fixture.nativeElement as HTMLElement, load, fellBack };
}

describe('PendingSchemaDiff', () => {
  it('asks for the two schemas of the diff the review holds, with its findings', () => {
    const { load } = render();

    expect(load).toHaveBeenCalledWith({
      from: 'schema-1',
      to: 'schema-2',
      divergences: versionDiff.divergences,
    });
  });

  it('shows the line diff under a heading that names the baseline', () => {
    const { host, fellBack } = render({ result: ready });

    expect(host.querySelector('h3')?.textContent).toContain('Changes from v1');
    expect(host.querySelector('cd-schema-diff')).not.toBeNull();
    expect(host.querySelector('[data-testid="diff-fallback"]')).toBeNull();
    expect(fellBack.at(-1)).toBe(false);
  });

  it('still shows the lines, and says no rule reports it, when the engine found nothing', () => {
    const { host } = render({ result: ready, diff: { ...versionDiff, divergences: [] } });

    expect(host.textContent).toContain('No compatibility rule reports a difference from v1');
    expect(host.querySelector('cd-schema-diff')).not.toBeNull();
  });

  it('shows a skeleton while the schemas load and does not claim a fall back', () => {
    const { host, fellBack } = render({ loading: true });

    expect(host.querySelector('[aria-busy="true"]')).not.toBeNull();
    expect(fellBack.at(-1)).toBe(false);
  });

  it.each([
    [
      'a change past the line limit',
      { status: 'too-large', reason: 'changes', limit: 1000 } as const,
      'More than 1,000 lines changed',
    ],
    [
      'a document past the length limit',
      { status: 'too-large', reason: 'lines', limit: 100000 } as const,
      'longer than 100,000 lines',
    ],
  ])('says so, and hands over to the summary, for %s', (_name, result, sentence) => {
    const { host, fellBack } = render({ result });
    const note = host.querySelector('[data-testid="diff-fallback"]')?.textContent;

    expect(note).toContain(sentence);
    expect(note).toContain('The summary is shown instead.');
    expect(host.querySelector('cd-schema-diff')).toBeNull();
    expect(fellBack.at(-1)).toBe(true);
  });

  it.each([
    ['load', 'No such schema.', 'could not be loaded (No such schema.)'],
    ['timeout', 'ignored', 'took too long and was stopped'],
    ['unavailable', 'no workers', 'could not run in this browser (no workers)'],
  ] as const)(
    'says so, and hands over to the summary, when the problem is %s',
    (reason, detail, sentence) => {
      const { host, fellBack } = render({ problem: { reason, detail } });

      expect(host.querySelector('[data-testid="diff-fallback"]')?.textContent).toContain(sentence);
      expect(fellBack.at(-1)).toBe(true);
    },
  );
});
