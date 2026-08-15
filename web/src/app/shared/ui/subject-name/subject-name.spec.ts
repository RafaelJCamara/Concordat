import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { SubjectName } from './subject-name';

// The component exists for one reason — a dotted name must not lose its leaf to an ellipsis —
// so the tests are about the seams and about the text surviving them intact.

describe('SubjectName', () => {
  let fixture: ComponentFixture<SubjectName>;

  function render(name: string): HTMLElement {
    fixture.componentRef.setInput('name', name);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [SubjectName] });
    fixture = TestBed.createComponent(SubjectName);
  });

  it('renders the name exactly, seams and all', () => {
    // The one assertion that must never bend. A break opportunity is invisible to
    // `textContent`, so a name that has picked up a space or lost a dot shows up here.
    expect(render('acme.e2e.PaymentTaken').textContent).toBe('acme.e2e.PaymentTaken');
  });

  it('offers a break after every dot', () => {
    const host = render('acme.e2e.PaymentTaken');

    // Three segments, three opportunities: the two seams plus the no-op before the first
    // segment. What matters is that `PaymentTaken` can start a line and `acme.` can end one.
    expect(host.querySelectorAll('wbr')).toHaveLength(3);
  });

  it('leaves the dot on the segment it follows', () => {
    // `acme.` / `orders` at a line end, not `acme` / `.orders` — the convention a path or a
    // URL is broken with, and the one that still reads as one name across two lines.
    const host = render('acme.orders');

    expect([...host.childNodes].map((node) => node.textContent).join('|')).toContain('acme.');
  });

  it('handles a name with no dots at all', () => {
    const host = render('orders');

    expect(host.textContent).toBe('orders');
    expect(host.querySelectorAll('wbr')).toHaveLength(1);
  });

  it('does not emit an empty segment for a trailing dot', () => {
    // The registry would not accept one, but a split on a trailing separator produces an
    // empty string, and an empty interpolation is a wasted DOM node in every row of a list.
    expect(render('acme.').textContent).toBe('acme.');
  });
});
