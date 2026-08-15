import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { StatusBadge, type StatusTone } from './status-badge';

// The component is four lines of template and one lookup, so there is exactly one thing worth
// testing: that a tone reaches the DOM as its own colour and that the text is always there to
// read. Both are accessibility properties rather than styling ones, which is why they are
// pinned rather than left to the eye.

@Component({
  imports: [StatusBadge],
  template: `<cd-status-badge [tone]="tone" [pulse]="pulse">Awaiting approval</cd-status-badge>`,
})
class Host {
  tone: StatusTone = 'neutral';
  pulse = false;
}

describe('StatusBadge', () => {
  function render(tone: StatusTone, pulse = false): HTMLElement {
    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.tone = tone;
    fixture.componentInstance.pulse = pulse;
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('always renders its label, whatever the tone', () => {
    // The tint is never the only signal. Colour alone does not survive greyscale, a projector,
    // or the readers who cannot distinguish the success and destructive hues — so the word has
    // to be there in every tone, not just the ones that look ambiguous.
    for (const tone of ['success', 'warning', 'destructive', 'info', 'neutral'] as const) {
      expect(render(tone).textContent).toContain('Awaiting approval');
    }
  });

  it('gives each tone its own colour', () => {
    expect(render('success').innerHTML).toContain('text-success');
    expect(render('warning').innerHTML).toContain('text-warning');
    expect(render('destructive').innerHTML).toContain('text-destructive');
    expect(render('info').innerHTML).toContain('text-info');
    expect(render('neutral').innerHTML).toContain('text-muted-foreground');
  });

  it('is neutral unless told otherwise', () => {
    // The safe default. A badge that defaulted to `destructive` would turn a forgotten input
    // into an alarm, which is the failure mode that costs someone a page at 3am.
    const fixture = TestBed.createComponent(StatusBadge);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).innerHTML).toContain('text-muted-foreground');
  });

  it('hides the dot from assistive tech', () => {
    // It restates the tone, and the tone restates the text. A screen reader that announced it
    // would add a third telling of the same fact.
    expect(render('success').querySelector('span span')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('pulses only when asked', () => {
    // Motion draws the eye to one row. Every row pulsing draws it nowhere, so this is opt-in
    // rather than a property of the tone.
    expect(render('success', false).innerHTML).not.toContain('animate-pulse-dot');
    expect(render('success', true).innerHTML).toContain('animate-pulse-dot');
  });
});
