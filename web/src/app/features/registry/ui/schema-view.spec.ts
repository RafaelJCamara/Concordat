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

  it('colours keys, values and literals apart', () => {
    // The five `--syntax-*` tokens the design system has carried since the port, finally
    // reaching the one screen they were defined for.
    const host = render(document({ text: '{"name":"orders","count":3,"open":true,"note":null}' }));

    const classOf = (text: string) =>
      [...host.querySelectorAll('code span')].find((span) => span.textContent === text)?.className;

    expect(classOf('"name"')).toBe('text-syntax-property');
    expect(classOf('"orders"')).toBe('text-syntax-string');
    expect(classOf('3')).toBe('text-syntax-number');
    expect(classOf('true')).toBe('text-syntax-boolean');
    expect(classOf('null')).toBe('text-syntax-keyword');
  });

  it('leaves the document byte-identical once the colours are stripped', () => {
    // The property that keeps highlighting honest. A reader copies what is on screen back into
    // a file, so a scanner that dropped a brace or doubled a space would be worse than no
    // colour at all.
    const text = '{"a":[1,-2.5,1e3],"b":{"c":"x"},"d":[true,false,null]}';
    const host = render(document({ text }));

    expect(host.querySelector('code')?.textContent).toBe(JSON.stringify(JSON.parse(text), null, 2));
  });

  it('is not fooled by a quote a schema author escaped', () => {
    // The case a regex highlighter cannot get right, and the reason this one reads left to
    // right. `pattern` holding an escaped quote is ordinary JSON Schema; a scanner that ended
    // the string there would colour the rest of the document as one long string.
    const host = render(document({ text: '{"pattern":"a\\"b","type":"string"}' }));

    const properties = [...host.querySelectorAll('.text-syntax-property')].map(
      (span) => span.textContent,
    );

    expect(properties).toEqual(['"pattern"', '"type"']);
  });

  it('renders markup inside a value as text even when it is highlighted', () => {
    // The XSS case again, now that the output is a tree of spans rather than one text node.
    // Every character still arrives through interpolation, which escapes.
    const host = render(document({ text: '{"title":"<img src=x onerror=alert(1)>"}' }));

    expect(host.querySelector('img')).toBeNull();
    expect(host.querySelector('code')?.textContent).toContain('<img src=x onerror=alert(1)>');
  });

  it('leaves an unscannable document in the plain foreground', () => {
    // Protobuf and anything that failed to parse come back as a single unclassed run. Dimming
    // them as though they were punctuation would be worse than not colouring them.
    const host = render(document({ format: 'protobuf', text: 'message Order { string id = 1; }' }));
    const spans = host.querySelectorAll('code span');

    expect(spans).toHaveLength(1);
    expect(spans[0]?.className).toBe('');
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
