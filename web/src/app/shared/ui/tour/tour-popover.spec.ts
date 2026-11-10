import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TourPopover } from './tour-popover';

// The behaviour that makes the tour safe to leave on for someone using a keyboard or a screen
// reader: it is announced, it can be left, it takes nothing away from the page, and it does not
// hold on to focus. The numbers behind where it sits are `tour-placement.spec.ts`.

describe('TourPopover', () => {
  let fixture: ComponentFixture<TourPopover>;
  let root: HTMLElement;
  let target: HTMLElement;

  const events = { advanced: 0, previous: 0, skipped: 0 };

  beforeEach(() => {
    events.advanced = events.previous = events.skipped = 0;

    // The page the tour sits over: something to point at, and something else to focus.
    target = document.createElement('a');
    target.setAttribute('href', '#target');
    target.textContent = 'Subjects';
    target.getBoundingClientRect = () => new DOMRect(16, 120, 224, 44);
    document.body.append(target);

    fixture = TestBed.createComponent(TourPopover);
    fixture.componentRef.setInput('title', 'Subjects');
    fixture.componentRef.setInput('body', 'Every message type has a subject.');
    fixture.componentRef.setInput('step', 2);
    fixture.componentRef.setInput('total', 5);
    fixture.componentRef.setInput('target', target);
    fixture.componentInstance.advanced.subscribe(() => events.advanced++);
    fixture.componentInstance.previous.subscribe(() => events.previous++);
    fixture.componentInstance.skipped.subscribe(() => events.skipped++);
    root = fixture.nativeElement as HTMLElement;
  });

  afterEach(() => {
    fixture.destroy();
    document.body.replaceChildren();
  });

  /** Renders, and lets the work that waits for a render (focus, measuring) happen. */
  async function show() {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  const dialog = () => root.querySelector<HTMLElement>('[role="dialog"]') as HTMLElement;
  const button = (name: string) =>
    [...root.querySelectorAll('button')].find((b) => b.textContent?.trim() === name) ?? null;
  const live = () => root.querySelector('[role="status"]') as HTMLElement;

  /**
   * Holds back animation frames until the test says the frame has come, so that a measurement
   * does not happen in the middle of the render that Angular schedules on the same clock.
   */
  function nextFrame(): () => void {
    const pending: FrameRequestCallback[] = [];
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((callback) => {
      pending.push(callback);
      return pending.length;
    });
    return () => pending.splice(0).forEach((callback) => callback(0));
  }

  function escape(on: EventTarget = document) {
    const event = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
    on.dispatchEvent(event);
    return event;
  }

  describe('what it says', () => {
    it('is a dialog named by the step and described by its words', async () => {
      await show();

      const name = root.querySelector(`#${dialog().getAttribute('aria-labelledby')}`);
      const description = root.querySelector(`#${dialog().getAttribute('aria-describedby')}`);

      expect(name?.textContent?.trim()).toBe('Subjects');
      expect(description?.textContent?.trim()).toBe('Every message type has a subject.');
    });

    it('is not modal: it does not claim the page is unavailable', async () => {
      await show();

      expect(dialog().getAttribute('aria-modal')).toBeNull();
      expect(document.querySelector('[inert]')).toBeNull();
    });

    it('says where the person is in the tour', async () => {
      await show();

      expect(root.textContent).toContain('Step 2 of 5');
    });

    it('has Skip, Back and Next as real buttons', async () => {
      await show();

      for (const name of ['Skip tour', 'Back', 'Next']) {
        const found = button(name);
        expect(found?.tagName).toBe('BUTTON');
        expect(found?.getAttribute('type')).toBe('button');
      }
    });

    it('has no Back on the first step', async () => {
      fixture.componentRef.setInput('step', 1);
      await show();

      expect(button('Back')).toBeNull();
      expect(button('Next')).not.toBeNull();
    });

    it('says Done, and not Next, on the last step', async () => {
      fixture.componentRef.setInput('step', 5);
      await show();

      expect(button('Done')).not.toBeNull();
      expect(button('Next')).toBeNull();
    });

    it('still offers Skip on the last step, so it can be left at any step', async () => {
      fixture.componentRef.setInput('step', 5);
      await show();

      expect(button('Skip tour')).not.toBeNull();
    });
  });

  describe('the buttons', () => {
    it('Next advances, Back goes back, Skip skips', async () => {
      await show();

      button('Next')?.click();
      expect(events).toEqual({ advanced: 1, previous: 0, skipped: 0 });

      button('Back')?.click();
      expect(events).toEqual({ advanced: 1, previous: 1, skipped: 0 });

      button('Skip tour')?.click();
      expect(events).toEqual({ advanced: 1, previous: 1, skipped: 1 });
    });

    it('Done finishes by the same output as Next', async () => {
      fixture.componentRef.setInput('step', 5);
      await show();

      button('Done')?.click();

      expect(events.advanced).toBe(1);
    });
  });

  describe('announcing', () => {
    it('is silent as it opens, because focus on the dialog says it already', async () => {
      await show();

      expect(live().textContent?.trim()).toBe('');
      expect(live().getAttribute('aria-live')).toBe('polite');
    });

    it('announces the new step, politely, when the step changes', async () => {
      await show();

      fixture.componentRef.setInput('title', 'Approvals');
      fixture.componentRef.setInput('body', 'Breaking changes wait.');
      fixture.componentRef.setInput('step', 3);
      await show();

      expect(live().textContent?.trim()).toBe('Step 3 of 5: Approvals. Breaking changes wait.');
    });
  });

  describe('focus', () => {
    it('moves to the dialog as it opens, so a keyboard or screen reader user arrives at it', async () => {
      await show();

      expect(document.activeElement).toBe(dialog());
    });

    it('does not trap Tab: the key is left alone and focus can leave', async () => {
      await show();

      const tab = new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true });
      dialog().dispatchEvent(tab);
      button('Next')?.dispatchEvent(tab);

      expect(tab.defaultPrevented).toBe(false);

      // And focus really can go to the page, and stay there.
      target.focus();
      await show();
      expect(document.activeElement).toBe(target);
    });

    it('stays on the button that was pressed when the step changes', async () => {
      await show();

      button('Next')?.focus();
      fixture.componentRef.setInput('step', 3);
      await show();

      expect(document.activeElement).toBe(button('Next'));
    });

    it('comes back to the dialog when the button that had focus has gone', async () => {
      await show();

      // Back is not drawn on the first step: pressing it on the second removes the button
      // that was focused, and the browser would otherwise drop focus on the page.
      button('Back')?.focus();
      fixture.componentRef.setInput('step', 1);
      await show();

      expect(document.activeElement).toBe(dialog());
    });

    it('leaves focus alone when the person has moved it into the page', async () => {
      await show();

      target.focus();
      fixture.componentRef.setInput('step', 3);
      await show();

      expect(document.activeElement).toBe(target);
    });
  });

  describe('Escape', () => {
    it('skips the tour from the dialog', async () => {
      await show();

      escape(button('Next') as HTMLElement);

      expect(events.skipped).toBe(1);
    });

    it('skips the tour when nothing in particular has focus', async () => {
      await show();
      (document.activeElement as HTMLElement).blur();

      escape();

      expect(events.skipped).toBe(1);
    });

    it('leaves an Escape that belongs to something else on the page alone', async () => {
      await show();
      const field = document.createElement('input');
      document.body.append(field);
      field.focus();

      escape(field);

      expect(events.skipped).toBe(0);
    });

    it('leaves an Escape that something has already handled', async () => {
      await show();
      // In the capture phase on the window, which is before the document's listener hears it.
      const handled = (event: Event) => event.preventDefault();
      window.addEventListener('keydown', handled, { capture: true, once: true });

      escape();

      expect(events.skipped).toBe(0);
    });

    it('stops listening once it is gone', async () => {
      await show();
      fixture.destroy();

      escape();

      expect(events.skipped).toBe(0);
    });
  });

  describe('the page underneath', () => {
    it('is not covered by anything that takes clicks', async () => {
      await show();

      const ring = root.querySelector('[data-tour-ring]') as HTMLElement;

      expect(ring.getAttribute('aria-hidden')).toBe('true');
      expect(ring.className).toContain('pointer-events-none');
    });
  });

  describe('where it is', () => {
    it('draws a ring a little larger than the target', async () => {
      await show();

      const ring = root.querySelector('[data-tour-ring]') as HTMLElement;

      expect(ring.style.left).toBe('12px');
      expect(ring.style.top).toBe('116px');
      expect(ring.style.width).toBe('232px');
      expect(ring.style.height).toBe('52px');
    });

    it('sits beside the target on a wide window', async () => {
      await show();

      expect(dialog().style.left).toBe(`${16 + 224 + 12}px`);
      expect(dialog().className).toContain('w-80');
      expect(dialog().className).not.toContain('inset-x-2');
    });

    it('is a sheet along the bottom on a narrow one, still pointing', async () => {
      fixture.componentRef.setInput('sheet', true);
      await show();

      expect(dialog().className).toContain('inset-x-2');
      expect(dialog().className).toContain('bottom-2');
      expect(dialog().style.left).toBe('');
      expect(root.querySelector('[data-tour-ring]')).not.toBeNull();
    });

    it('is a sheet along the top when the target is in the lower half of a narrow window', async () => {
      target.getBoundingClientRect = () => new DOMRect(16, 700, 40, 40);
      fixture.componentRef.setInput('sheet', true);
      await show();

      expect(dialog().className).toContain('top-2');
      expect(dialog().className).not.toContain('bottom-2');
    });

    it('is centred, with no ring, when there is nothing to point at', async () => {
      fixture.componentRef.setInput('target', null);
      await show();

      expect(root.querySelector('[data-tour-ring]')).toBeNull();
      expect(dialog().className).toContain('-translate-x-1/2');
      expect(dialog().style.left).toBe('');
    });

    it('is shown, not transparent, once it has measured where to go', async () => {
      await show();

      expect(dialog().className).not.toContain('opacity-0');
    });

    it('follows the target when the window is resized', async () => {
      await show();

      target.getBoundingClientRect = () => new DOMRect(16, 300, 224, 44);
      const frame = nextFrame();
      window.dispatchEvent(new Event('resize'));
      frame();
      fixture.detectChanges();

      const ring = root.querySelector('[data-tour-ring]') as HTMLElement;
      expect(ring.style.top).toBe('296px');
    });

    it('stops following the window once it is gone', async () => {
      await show();
      const frame = nextFrame();
      fixture.destroy();
      const measured = vi.fn(() => new DOMRect(16, 300, 224, 44));
      target.getBoundingClientRect = measured;

      window.dispatchEvent(new Event('resize'));
      document.body.dispatchEvent(new Event('scroll'));
      frame();

      expect(measured).not.toHaveBeenCalled();
    });

    it('follows the target when anything scrolls', async () => {
      await show();

      target.getBoundingClientRect = () => new DOMRect(16, 20, 224, 44);
      const frame = nextFrame();
      document.body.dispatchEvent(new Event('scroll'));
      frame();
      fixture.detectChanges();

      // `scroll` does not bubble, so only a capturing listener on the document sees it from
      // a panel: this event is dispatched on the body and must still be heard.
      const ring = root.querySelector('[data-tour-ring]') as HTMLElement;
      expect(ring.style.top).toBe('16px');
    });
  });
});
