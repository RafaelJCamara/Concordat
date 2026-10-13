import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { EditorFinding } from '../../../domain/registry/editor-finding';
import { EditorFindings } from './editor-findings';

// A refusal and a piece of advice must not read the same. A list that renders both as red
// errors teaches people to ignore it, and the first time the registry disagrees with a piece
// of advice the whole panel loses its credibility.

const refusal: EditorFinding = {
  severity: 'refusal',
  code: 'schema_malformed',
  message: 'Not well-formed JSON: unexpected end.',
  position: { offset: 9, line: 1, column: 10 },
  length: 1,
};

const advice: EditorFinding = {
  severity: 'advice',
  code: 'ajv_metaschema',
  message: '/type must be a valid type.',
  position: null,
  length: 0,
};

function render(findings: readonly EditorFinding[]) {
  const fixture = TestBed.createComponent(EditorFindings);
  fixture.componentRef.setInput('findings', findings);
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement };
}

describe('EditorFindings', () => {
  it('draws nothing when there is nothing to say', () => {
    const { host } = render([]);

    expect(host.querySelector('ul')).toBeNull();
  });

  it('says in words which findings the registry will refuse, and which are only advice', () => {
    const { host } = render([refusal, advice]);

    expect(host.textContent).toContain('The registry will refuse this');
    expect(host.textContent).toContain('Worth checking');
  });

  it('offers a way to the position of a finding that has one', () => {
    const { fixture, host } = render([refusal]);
    const emitted: EditorFinding[] = [];
    fixture.componentInstance.reveal.subscribe((f) => emitted.push(f));

    host.querySelector('button')!.click();

    expect(host.querySelector('button')?.textContent).toContain('line 1, column 10');
    expect(emitted).toEqual([refusal]);
  });

  it('offers no button for a finding about the document as a whole', () => {
    const { host } = render([advice]);

    expect(host.querySelector('button')).toBeNull();
  });
});
