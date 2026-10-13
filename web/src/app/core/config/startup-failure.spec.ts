import { describe, expect, it } from 'vitest';
import { showStartupFailure } from './startup-failure';

describe('showStartupFailure', () => {
  it('says what went wrong, as an alert', () => {
    const root = document.createElement('cd-root');
    showStartupFailure(root, 'config.json: profile must be one of self-hosted, cloud.');

    expect(root.querySelector('[role="alert"]')?.textContent).toContain('could not start');
    expect(root.textContent).toContain('profile must be one of');
  });

  it('treats the message as text, never as markup', () => {
    // The message quotes a value from a file. A value from a file is not trusted markup.
    const root = document.createElement('cd-root');
    showStartupFailure(root, '<img src=x onerror=alert(1)>');

    expect(root.querySelector('img')).toBeNull();
    expect(root.textContent).toContain('<img src=x onerror=alert(1)>');
  });

  it('replaces whatever the root held', () => {
    const root = document.createElement('cd-root');
    root.textContent = 'Loading…';
    showStartupFailure(root, 'nope');

    expect(root.textContent).not.toContain('Loading');
  });
});
