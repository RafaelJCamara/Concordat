import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { Pagination, pageItems, type PageItem } from './pagination';

const shape = (items: readonly PageItem[]) =>
  items.map((item) => (item.kind === 'gap' ? '…' : String(item.page)));

describe('pageItems', () => {
  it('draws every page when they fit', () => {
    expect(shape(pageItems(1, 1))).toEqual(['1']);
    expect(shape(pageItems(3, 7))).toEqual(['1', '2', '3', '4', '5', '6', '7']);
  });

  it('draws the start, an ellipsis and the last page near the beginning', () => {
    expect(shape(pageItems(1, 100))).toEqual(['1', '2', '3', '4', '5', '…', '100']);
    expect(shape(pageItems(4, 100))).toEqual(['1', '2', '3', '4', '5', '…', '100']);
  });

  it('draws the first page, an ellipsis and the end near the end', () => {
    expect(shape(pageItems(100, 100))).toEqual(['1', '…', '96', '97', '98', '99', '100']);
    expect(shape(pageItems(97, 100))).toEqual(['1', '…', '96', '97', '98', '99', '100']);
  });

  it('draws the neighbours of a page in the middle, between two ellipses', () => {
    expect(shape(pageItems(50, 100))).toEqual(['1', '…', '49', '50', '51', '…', '100']);
    expect(shape(pageItems(5, 100))).toEqual(['1', '…', '4', '5', '6', '…', '100']);
    expect(shape(pageItems(96, 100))).toEqual(['1', '…', '95', '96', '97', '…', '100']);
  });

  it('is the same width wherever the current page is, so the row does not move under the pointer', () => {
    for (let current = 1; current <= 100; current++) {
      expect(pageItems(current, 100)).toHaveLength(7);
    }
  });

  it('always includes the current page, the first and the last', () => {
    for (let current = 1; current <= 40; current++) {
      const pages = pageItems(current, 40).flatMap((i) => (i.kind === 'page' ? [i.page] : []));

      expect(pages).toContain(current);
      expect(pages).toContain(1);
      expect(pages).toContain(40);
    }
  });

  it('never repeats a page or runs out of order', () => {
    for (let current = 1; current <= 40; current++) {
      const pages = pageItems(current, 40).flatMap((i) => (i.kind === 'page' ? [i.page] : []));

      expect(pages).toEqual([...new Set(pages)].sort((a, b) => a - b));
    }
  });

  it('only puts an ellipsis where pages were left out', () => {
    for (let current = 1; current <= 12; current++) {
      const items = pageItems(current, 12);

      items.forEach((item, i) => {
        if (item.kind === 'gap') {
          const before = items[i - 1];
          const after = items[i + 1];

          expect(before?.kind === 'page' && after?.kind === 'page').toBe(true);
          if (before?.kind === 'page' && after?.kind === 'page') {
            expect(after.page - before.page).toBeGreaterThan(1);
          }
        }
      });
    }
  });
});

describe('Pagination', () => {
  let fixture: ComponentFixture<Pagination>;
  let asked: number[];

  const host = () => fixture.nativeElement as HTMLElement;
  const buttons = () => [...host().querySelectorAll('button')];
  const named = (label: string) => buttons().find((b) => b.textContent?.trim() === label);
  const previous = () => host().querySelector<HTMLButtonElement>('[data-testid="pager-previous"]')!;
  const next = () => host().querySelector<HTMLButtonElement>('[data-testid="pager-next"]')!;

  function render(page: number, pageCount: number | null, hasNext = false) {
    fixture.componentRef.setInput('page', page);
    fixture.componentRef.setInput('pageCount', pageCount);
    fixture.componentRef.setInput('hasNext', hasNext);
    fixture.detectChanges();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [Pagination] });
    fixture = TestBed.createComponent(Pagination);
    asked = [];
    fixture.componentInstance.pageChange.subscribe((page) => asked.push(page));
  });

  it('is a landmark with a name', () => {
    render(1, 3);

    expect(host().querySelector('nav')?.getAttribute('aria-label')).toBe('Pagination');
    fixture.componentRef.setInput('label', 'Subject pages');
    fixture.detectChanges();
    expect(host().querySelector('nav')?.getAttribute('aria-label')).toBe('Subject pages');
  });

  it('marks the current page, by more than colour, and names every button for what it does', () => {
    render(2, 5);

    const current = host().querySelector('[aria-current="page"]');

    expect(current?.textContent?.trim()).toBe('2');
    expect(current?.getAttribute('aria-label')).toBe('Page 2');
    expect(host().querySelectorAll('[aria-current="page"]')).toHaveLength(1);
    expect(buttons().map((b) => b.getAttribute('aria-label') ?? b.textContent?.trim())).toEqual([
      'Previous',
      'Page 1',
      'Page 2',
      'Page 3',
      'Page 4',
      'Page 5',
      'Next',
    ]);
  });

  it('hides the ellipsis from a screen reader', () => {
    render(50, 100);

    const gaps = [...host().querySelectorAll('span')].filter((s) => s.textContent?.trim() === '…');

    expect(gaps).toHaveLength(2);
    expect(gaps.every((g) => g.getAttribute('aria-hidden') === 'true')).toBe(true);
  });

  it('asks for the page that was clicked', () => {
    render(2, 5);

    named('4')!.click();

    expect(asked).toEqual([4]);
  });

  it('asks for the next and the previous page', () => {
    render(3, 5);

    next().click();
    previous().click();

    expect(asked).toEqual([4, 2]);
  });

  it('asks for nothing when the current page is clicked', () => {
    render(2, 5);

    named('2')!.click();

    expect(asked).toEqual([]);
  });

  it('cannot go before the first page, and says so without dropping the focus', () => {
    render(1, 5);

    previous().click();

    expect(asked).toEqual([]);
    expect(previous().getAttribute('aria-disabled')).toBe('true');
    // aria-disabled and not disabled: a disabled button that had the focus would drop it to the page.
    expect(previous().disabled).toBe(false);
    expect(next().getAttribute('aria-disabled')).toBeNull();
  });

  it('cannot go past the last page', () => {
    render(5, 5);

    next().click();

    expect(asked).toEqual([]);
    expect(next().getAttribute('aria-disabled')).toBe('true');
    expect(next().disabled).toBe(false);
  });

  it('with no page count draws only the page it is on, and goes on while the page was full', () => {
    render(3, null, true);

    expect(host().querySelector('[data-testid="pager-current"]')?.textContent?.trim()).toBe(
      'Page 3',
    );
    expect(buttons()).toHaveLength(2);

    next().click();

    expect(asked).toEqual([4]);
  });

  it('with no page count stops at a page that was not full', () => {
    render(3, null, false);

    next().click();

    expect(asked).toEqual([]);
    expect(next().getAttribute('aria-disabled')).toBe('true');
  });

  it('keeps the same button element for Next across a page change, so the focus is not lost', () => {
    render(2, 10);
    const before = next();
    before.focus();

    render(3, 10);

    expect(next()).toBe(before);
  });
});
