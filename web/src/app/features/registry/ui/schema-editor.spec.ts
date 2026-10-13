import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { describe, expect, it } from 'vitest';
import { MONACO_LOADER, SchemaEditor } from './schema-editor';
import type { MonacoApi } from './monaco-loader';

// The editor is a form control, and the page never learns what is behind it. These pin the
// contract that makes that safe: what goes in comes out unchanged, and edits reach the form —
// and that the textarea is a working editor, both while Monaco is on its way and for good if it
// never arrives. Monaco itself is exercised in a real browser; there is no layout engine here to
// give it a size.

@Component({
  imports: [ReactiveFormsModule, SchemaEditor],
  template: `<cd-schema-editor inputId="doc" [formControl]="control" />`,
})
class Host {
  readonly control = new FormControl('', { nonNullable: true });
}

type Loader = () => Promise<MonacoApi>;

const never: Loader = () => new Promise<MonacoApi>(() => {});
const refused: Loader = () => Promise.reject(new Error('chunk could not be fetched'));

function render(loader: Loader = never) {
  TestBed.configureTestingModule({ providers: [{ provide: MONACO_LOADER, useValue: loader }] });

  const fixture = TestBed.createComponent(Host);
  fixture.detectChanges();

  const root = fixture.nativeElement as HTMLElement;

  return {
    fixture,
    root,
    area: () => root.querySelector<HTMLTextAreaElement>('#doc')!,
    control: fixture.componentInstance.control,
  };
}

describe('SchemaEditor', () => {
  describe('as a textarea', () => {
    it('shows what the form holds, exactly', () => {
      const { fixture, area, control } = render();
      control.setValue('{\n  "a": 1.0\n}');
      fixture.detectChanges();

      expect(area().value).toBe('{\n  "a": 1.0\n}');
    });

    it('reports each edit to the form', () => {
      const { area, control } = render();
      area().value = '{"x":1}';
      area().dispatchEvent(new Event('input'));

      expect(control.value).toBe('{"x":1}');
      expect(control.dirty).toBe(true);
    });

    it('marks the form touched when it loses focus', () => {
      const { area, control } = render();
      area().dispatchEvent(new Event('blur'));

      expect(control.touched).toBe(true);
    });

    it('follows the form being disabled', () => {
      const { fixture, area, control } = render();
      control.disable();
      fixture.detectChanges();

      expect(area().disabled).toBe(true);
    });

    it('is reachable by the id the page labels it with', () => {
      // `<label for="doc">` has to land on the thing that takes focus and input.
      const { area } = render();

      expect(area().id).toBe('doc');
    });

    it('puts the caret where a finding points', () => {
      const { fixture, area, control } = render();
      control.setValue('{"type": "bogus"}');
      fixture.detectChanges();

      fixture.debugElement.children[0]!.componentInstance.reveal(10);

      expect(area().selectionStart).toBe(10);
    });
  });

  describe('while Monaco is on its way', () => {
    it('is a working textarea, with nothing said about it', async () => {
      // The screen is usable the moment it renders; the rich editor is an upgrade, not a
      // precondition.
      const { fixture, area, control, root } = render(never);
      await fixture.whenStable();

      area().value = '{"typed":"early"}';
      area().dispatchEvent(new Event('input'));

      expect(control.value).toBe('{"typed":"early"}');
      expect(root.querySelector('[role="status"]')).toBeNull();
      expect(root.querySelector<HTMLElement>('[data-testid="monaco-host"]')?.hidden).toBe(true);
    });
  });

  describe('when Monaco cannot be loaded', () => {
    it('stays a textarea and says so in one sentence', async () => {
      // A chunk that cannot be fetched, a cache that served half a deploy. The person is here
      // to register a schema, not to debug a bundler.
      const { fixture, area, root } = render(refused);
      await fixture.whenStable();
      fixture.detectChanges();

      expect(area()).not.toBeNull();
      expect(root.querySelector('[role="status"]')?.textContent).toContain(
        'could not be loaded, so this is the plain editor',
      );
    });

    it('keeps taking edits', async () => {
      const { fixture, area, control } = render(refused);
      await fixture.whenStable();
      fixture.detectChanges();

      area().value = '{"still":"works"}';
      area().dispatchEvent(new Event('input'));

      expect(control.value).toBe('{"still":"works"}');
    });

    it('keeps what was already written', async () => {
      const { fixture, area, control } = render(refused);
      control.setValue('{"kept":true}');
      await fixture.whenStable();
      fixture.detectChanges();

      expect(area().value).toBe('{"kept":true}');
    });
  });
});
