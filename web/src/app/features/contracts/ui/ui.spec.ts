import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import type { ConsumeBinding, PublishBinding } from '../../../domain/contracts/contract';
import { BindingList } from './binding-list';
import { EnforcementBadge } from './enforcement-badge';

// The presentational pieces of the contract screens: how a mode is drawn, and how a binding is
// read. One file for both because each is small, and neither has behaviour beyond rendering.

const publish: PublishBinding = {
  exchange: 'orders',
  routingKeyPattern: 'orders.*',
  brokerId: null,
  virtualHost: '/',
  precedence: null,
  subjects: [{ subject: 'orders.created', selector: '>=2' }],
};

const consume: ConsumeBinding = {
  queue: 'billing.orders',
  brokerId: null,
  virtualHost: '/billing',
  subjects: [{ subject: 'orders.created', selector: 'latest' }],
};

function renderBindings(
  publishes: readonly PublishBinding[],
  consumes: readonly ConsumeBinding[],
): HTMLElement {
  TestBed.configureTestingModule({ providers: [provideRouter([])] });
  const fixture = TestBed.createComponent(BindingList);
  fixture.componentRef.setInput('publishes', publishes);
  fixture.componentRef.setInput('consumes', consumes);
  fixture.detectChanges();

  return fixture.nativeElement as HTMLElement;
}

describe('EnforcementBadge', () => {
  function render(mode: 'OFF' | 'MONITOR' | 'ENFORCE'): HTMLElement {
    const fixture = TestBed.createComponent(EnforcementBadge);
    fixture.componentRef.setInput('mode', mode);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  it.each(['OFF', 'MONITOR', 'ENFORCE'] as const)('always shows the word %s', (mode) => {
    // The tone is for scanning and the token is for reading: OFF and ENFORCE are not ordered by
    // colour, so the badge never leans on colour alone.
    expect(render(mode).textContent).toContain(mode);
  });

  it('says in a tooltip what ENFORCE does', () => {
    expect(render('ENFORCE').querySelector('[title]')?.getAttribute('title')).toContain('Refuses');
  });

  it('says in a tooltip that MONITOR lets the message through', () => {
    expect(render('MONITOR').querySelector('[title]')?.getAttribute('title')).toContain(
      'lets the message through',
    );
  });

  it('tones the three differently', () => {
    const html = (['OFF', 'MONITOR', 'ENFORCE'] as const).map((m) => render(m).innerHTML);

    expect(new Set(html.map((h) => h.match(/bg-[a-z-]+\/10|bg-muted/)?.[0])).size).toBe(3);
  });
});

describe('BindingList', () => {
  it('counts each kind', () => {
    const host = renderBindings([publish], [consume]);

    expect(host.textContent).toContain('Publishes · 1');
    expect(host.textContent).toContain('Consumes · 1');
  });

  it('shows the exchange, the pattern and the virtual host', () => {
    const host = renderBindings([publish], []);

    expect(host.textContent).toContain('orders');
    expect(host.textContent).toContain('orders.*');
    expect(host.textContent).toContain('vhost /');
  });

  it('links each subject to its own screen, with the selector beside it', () => {
    const host = renderBindings([publish], []);
    const link = host.querySelector('a[href="/subjects/orders.created"]');

    expect(link?.textContent?.replace(/\s+/g, '')).toBe('orders.created@>=2');
  });

  it('shows precedence only when one was declared', () => {
    expect(renderBindings([{ ...publish, precedence: 3 }], []).textContent).toContain(
      'precedence 3',
    );
  });

  it('does not show a precedence column of dashes when none was', () => {
    expect(renderBindings([publish], []).textContent).not.toContain('precedence');
  });

  it('shows a zero precedence, which is a precedence', () => {
    expect(renderBindings([{ ...publish, precedence: 0 }], []).textContent).toContain(
      'precedence 0',
    );
  });

  it('says a contract with no publish binding governs no route yet', () => {
    expect(renderBindings([], [consume]).textContent).toContain('governs no route yet');
  });

  it('shows the queue and its virtual host for a consume binding', () => {
    const host = renderBindings([], [consume]);

    expect(host.textContent).toContain('billing.orders');
    expect(host.textContent).toContain('vhost /billing');
  });

  it('says plainly when there are no consume bindings', () => {
    expect(renderBindings([publish], []).textContent).toContain('No consume bindings');
  });
});
