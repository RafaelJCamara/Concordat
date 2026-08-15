import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { SchemaDocument } from '../../../domain/registry/schema';
import { SchemaView } from './schema-view';

// The security property is the point of this file. Schema text is user content — one team's
// document rendered in another team's browser — and the prototype highlighted it with a regex
// and `dangerouslySetInnerHTML`. The test that matters is that a document containing markup
// arrives as text.

function document(overrides: Partial<SchemaDocument> = {}): SchemaDocument {
  return {
    schemaId: 'abcdef0123456789abcdef0123456789',
    format: 'json',
    text: '{"type":"object"}',
    references: [],
    ...overrides,
  };
}

describe('SchemaView', () => {
  let fixture: ComponentFixture<SchemaView>;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [SchemaView] });
    fixture = TestBed.createComponent(SchemaView);
  });

  function render(value: SchemaDocument): HTMLElement {
    fixture.componentRef.setInput('document', value);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  it('renders a document containing markup as text, not as elements', () => {
    // The XSS case, stated as a test. A schema author controls this string.
    const host = render(
      document({ text: '{"title":"<img src=x onerror=alert(1)>"}', format: 'json' }),
    );

    expect(host.querySelector('img')).toBeNull();
    expect(host.querySelector('code')?.textContent).toContain('<img src=x onerror=alert(1)>');
  });

  it('indents JSON so a nested schema is readable', () => {
    const host = render(document({ text: '{"type":"object","required":["id"]}' }));

    expect(host.querySelector('code')?.textContent).toBe(
      '{\n  "type": "object",\n  "required": [\n    "id"\n  ]\n}',
    );
  });

  it('leaves protobuf alone, because it is already line-oriented', () => {
    // `JSON.parse` would throw on it, and re-indenting a `.proto` is not a thing this
    // component knows how to do.
    const text = 'syntax = "proto3";\n\nmessage Order {\n  string id = 1;\n}\n';
    const host = render(document({ format: 'protobuf', text }));

    expect(host.querySelector('code')?.textContent).toBe(text);
  });

  it('shows the original text when a document does not parse', () => {
    // Better than an error: the reader came here to see what was registered, and the
    // registry accepted this, so showing it is the honest answer.
    const host = render(document({ text: 'not json at all' }));

    expect(host.querySelector('code')?.textContent).toBe('not json at all');
  });

  it('lists references when there are any', () => {
    const host = render(
      document({ references: [{ name: 'Money', subject: 'common.money', version: 2 }] }),
    );

    expect(host.textContent).toContain('Money');
    expect(host.textContent).toContain('common.money');
    expect(host.textContent).toContain('v2');
  });

  it('says nothing about references when there are none', () => {
    // An empty "References" heading is a question the reader then has to answer.
    const host = render(document({ references: [] }));

    expect(host.textContent).not.toContain('References');
  });

  it('makes the scrolling region reachable by keyboard', () => {
    // A scrollable region that cannot be focused is a WCAG 2.1.1 failure: there is no other
    // way to reach the overflow without a mouse.
    const host = render(document());
    const pre = host.querySelector('pre');

    expect(pre?.getAttribute('tabindex')).toBe('0');
    expect(pre?.getAttribute('aria-label')).toContain('Schema document');
  });
});
