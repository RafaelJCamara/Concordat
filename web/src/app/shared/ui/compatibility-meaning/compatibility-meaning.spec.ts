import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import {
  COMPATIBILITY_DIRECTION_MEANING,
  COMPATIBILITY_FINDING_SURFACE_NOTE,
  COMPATIBILITY_MODE_MEANING,
  COMPATIBILITY_MODES,
  COMPATIBILITY_SURFACE_MEANING,
  COMPATIBILITY_SURFACES,
} from '../../../domain/registry/compatibility';
import { CompatibilityGuide } from './compatibility-guide';
import { CompatibilityMeaning } from './compatibility-meaning';

// What these pin is the accessibility of the pattern, since the wording is pinned in the domain:
// that the words reach the DOM, that a select can be described by them without being renamed by
// them, that the button is a real button with its state exposed, and that a closed guide holds no
// token (the e2e specs match tokens by exact text, hidden or not).

@Component({
  imports: [CompatibilityMeaning],
  template: `
    <label for="m">Mode</label>
    <select id="m" [attr.aria-describedby]="'m-help'">
      <option value="">Inherit</option>
    </select>
    <cd-compatibility-meaning axis="mode" [value]="mode" textId="m-help" />
    <cd-compatibility-meaning axis="surface" [value]="surface" textId="s-help" />
  `,
})
class MeaningHost {
  mode: string | null = '';
  surface: string | null = '';
}

describe('CompatibilityMeaning', () => {
  function render(mode: string | null, surface: string | null): HTMLElement {
    const fixture = TestBed.createComponent(MeaningHost);
    fixture.componentInstance.mode = mode;
    fixture.componentInstance.surface = surface;
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  it('says what every mode means, and the select it describes points at that sentence', () => {
    for (const mode of COMPATIBILITY_MODES) {
      const page = render(mode, '');
      const sentence = page.querySelector('#m-help') as HTMLElement;

      expect(sentence.textContent?.trim(), mode).toBe(COMPATIBILITY_MODE_MEANING[mode]);
      expect(sentence.hidden, mode).toBe(false);
      expect(page.querySelector('select')?.getAttribute('aria-describedby')).toBe(sentence.id);
    }
  });

  it('says what every surface means', () => {
    for (const surface of COMPATIBILITY_SURFACES) {
      const sentence = render('', surface).querySelector('#s-help') as HTMLElement;

      expect(sentence.textContent?.trim(), surface).toBe(COMPATIBILITY_SURFACE_MEANING[surface]);
    }
  });

  it('says nothing for the inherit choice, and says it by hiding rather than by an empty paragraph', () => {
    for (const empty of ['', null]) {
      const sentence = render(empty, empty).querySelector('#m-help') as HTMLElement;

      expect(sentence.hidden).toBe(true);
      expect(sentence.textContent?.trim()).toBe('');
    }
  });

  it('says nothing for a token it was never taught, rather than a guess', () => {
    expect((render('BACKWARDTRANSITIVE', '').querySelector('#m-help') as HTMLElement).hidden).toBe(
      true,
    );
    // Not inherited from Object.prototype either.
    expect((render('constructor', '').querySelector('#m-help') as HTMLElement).hidden).toBe(true);
  });

  it('does not change the select’s accessible name, which the e2e specs find it by', () => {
    const page = render('BACKWARD', 'WIRE_JSON');
    const select = page.querySelector('select') as HTMLSelectElement;

    expect(select.labels?.[0]?.textContent).toBe('Mode');
    expect(select.hasAttribute('aria-label')).toBe(false);
    expect(select.hasAttribute('aria-labelledby')).toBe(false);
  });
});

@Component({
  imports: [CompatibilityGuide],
  template: `
    <cd-compatibility-guide
      [modes]="modes"
      [surfaces]="surfaces"
      [directions]="directions"
      [findings]="findings"
    />
  `,
})
class GuideHost {
  modes: readonly (string | null)[] = [];
  surfaces: readonly (string | null)[] = [];
  directions: readonly (string | null)[] = [];
  findings = false;
}

describe('CompatibilityGuide', () => {
  function render(setup: Partial<GuideHost>): {
    page: HTMLElement;
    button: HTMLButtonElement;
    open: () => void;
  } {
    const fixture = TestBed.createComponent(GuideHost);
    Object.assign(fixture.componentInstance, setup);
    fixture.detectChanges();
    const page = fixture.nativeElement as HTMLElement;
    const button = page.querySelector('button') as HTMLButtonElement;

    return {
      page,
      button,
      open: () => {
        button.click();
        fixture.detectChanges();
      },
    };
  }

  it('is a real button, closed to begin with, that says it is closed', () => {
    const { button } = render({ modes: ['BACKWARD'] });

    expect(button.tagName).toBe('BUTTON');
    expect(button.type).toBe('button');
    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(button.hasAttribute('aria-controls')).toBe(false);
    expect(button.textContent).toContain('What do these mean?');
  });

  it('takes a name for the button, so a screen with two of them does not read the same words twice', () => {
    const fixture = TestBed.createComponent(CompatibilityGuide);
    fixture.componentRef.setInput('modes', ['NONE']);
    fixture.componentRef.setInput('label', 'What does this default mean?');
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('button').textContent).toContain(
      'What does this default mean?',
    );
  });

  it('puts no token in the page while closed', () => {
    const { page } = render({
      modes: [...COMPATIBILITY_MODES],
      surfaces: [...COMPATIBILITY_SURFACES],
    });

    for (const token of [...COMPATIBILITY_MODES, ...COMPATIBILITY_SURFACES]) {
      expect(page.textContent, token).not.toContain(token);
    }
  });

  it('opens on a click, which is also what Enter, Space and a tap send to a button', () => {
    const { page, button, open } = render({ modes: ['FULL_TRANSITIVE'], surfaces: ['SOURCE'] });

    open();

    expect(button.getAttribute('aria-expanded')).toBe('true');
    const panel = page.querySelector(`#${button.getAttribute('aria-controls')}`);
    expect(panel).not.toBeNull();
    expect(panel?.textContent).toContain(COMPATIBILITY_MODE_MEANING.FULL_TRANSITIVE);
    expect(panel?.textContent).toContain(COMPATIBILITY_SURFACE_MEANING.SOURCE);
  });

  it('keeps one name open and closed, because aria-expanded is what says which', () => {
    const { button, open } = render({ modes: ['NONE'] });
    const closed = button.textContent?.trim();

    open();

    expect(button.textContent?.trim()).toBe(closed);
  });

  it('closes again, and the panel goes with it', () => {
    const { page, button, open } = render({ modes: ['NONE'] });

    open();
    open();

    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(page.querySelector('dl')).toBeNull();
  });

  it('explains only the tokens it was given, each once, in the vocabulary’s order', () => {
    const { page, open } = render({
      modes: ['FORWARD', 'BACKWARD', 'FORWARD', null],
      surfaces: ['SOURCE', 'WIRE'],
    });

    open();

    const terms = [...page.querySelectorAll('dt')].map((dt) => dt.textContent?.trim());
    expect(terms).toEqual(['BACKWARD', 'FORWARD', 'WIRE', 'SOURCE']);
  });

  it('leaves out a token it does not know, and a group with nothing in it', () => {
    const { page, open } = render({ modes: ['BACKWARDTRANSITIVE'], surfaces: ['WIRE_JSON'] });

    open();

    expect(page.textContent).not.toContain('BACKWARDTRANSITIVE');
    expect(page.textContent).not.toContain('Who breaks');
    expect([...page.querySelectorAll('dt')].map((dt) => dt.textContent?.trim())).toEqual([
      'WIRE_JSON',
    ]);
  });

  it('uses a definition list, so a screen reader hears each token with its meaning', () => {
    const { page, open } = render({ modes: ['BACKWARD'], directions: ['FORWARD'] });

    open();

    expect(page.querySelectorAll('dl')).toHaveLength(2);
    expect(page.querySelectorAll('dt')).toHaveLength(2);
    expect(page.querySelectorAll('dd')).toHaveLength(2);
    expect(page.textContent).toContain(COMPATIBILITY_DIRECTION_MEANING.FORWARD);
  });

  it('says how to read a surface on a finding only when asked, because the token means two things', () => {
    const policy = render({ surfaces: ['WIRE_JSON'] });
    policy.open();
    expect(policy.page.textContent).not.toContain(COMPATIBILITY_FINDING_SURFACE_NOTE);

    const finding = render({ surfaces: ['WIRE_JSON'], findings: true });
    finding.open();
    expect(finding.page.textContent).toContain(COMPATIBILITY_FINDING_SURFACE_NOTE);
  });

  it('wraps a long token and a long sentence rather than widening the page', () => {
    // At 390px a 19-character monospace token plus its sentence must not push the page sideways.
    // jsdom does not lay out, so this pins the classes that do the work; the browser spec measures.
    const { page, open } = render({ modes: ['BACKWARD_TRANSITIVE'] });

    open();

    expect(page.querySelector('dt')?.className).toContain('break-all');
    expect(page.querySelector('dd')?.className).toContain('break-words');
  });
});
