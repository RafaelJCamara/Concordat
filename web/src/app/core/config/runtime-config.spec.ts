import { describe, expect, it } from 'vitest';
import { RuntimeConfigError, fetchRuntimeConfig, parseRuntimeConfig } from './runtime-config';

// This file decides where a bearer token is sent, so the tests are about what it refuses as
// much as what it takes — and about the one asymmetry that matters: a missing file is fine and a
// wrong one is not.

describe('parseRuntimeConfig', () => {
  it('takes every member it knows', () => {
    expect(
      parseRuntimeConfig({
        apiBaseUrl: 'https://registry.example.com',
        profile: 'cloud',
        tenant: 'acme',
        defaultEnvironment: 'prod',
      }),
    ).toEqual({
      apiBaseUrl: 'https://registry.example.com',
      profile: 'cloud',
      tenant: 'acme',
      defaultEnvironment: 'prod',
    });
  });

  it('says nothing about a member that is not there', () => {
    // So the defaults, not a guess, fill the gap.
    expect(parseRuntimeConfig({})).toEqual({});
    expect(parseRuntimeConfig({ profile: 'self-hosted' })).toEqual({ profile: 'self-hosted' });
  });

  it('ignores a member it has never heard of', () => {
    // A newer deployment's file must not stop an older bundle.
    expect(parseRuntimeConfig({ profile: 'cloud', somethingNew: [1, 2] })).toEqual({
      profile: 'cloud',
    });
  });

  it.each([null, 'text', 7, [], true])('refuses %j as a whole file', (raw) => {
    expect(() => parseRuntimeConfig(raw)).toThrow(RuntimeConfigError);
  });

  describe('apiBaseUrl', () => {
    it('is empty for same-origin', () => {
      expect(parseRuntimeConfig({ apiBaseUrl: '' })).toEqual({ apiBaseUrl: '' });
    });

    it('takes a path prefix, without its trailing slash', () => {
      expect(parseRuntimeConfig({ apiBaseUrl: '/registry/' })).toEqual({ apiBaseUrl: '/registry' });
      expect(parseRuntimeConfig({ apiBaseUrl: '/a/b' })).toEqual({ apiBaseUrl: '/a/b' });
    });

    it('takes an http(s) origin, normalised, without its trailing slash', () => {
      expect(parseRuntimeConfig({ apiBaseUrl: 'https://Registry.Example.com/' })).toEqual({
        apiBaseUrl: 'https://registry.example.com',
      });
      expect(parseRuntimeConfig({ apiBaseUrl: 'http://127.0.0.1:5062' })).toEqual({
        apiBaseUrl: 'http://127.0.0.1:5062',
      });
      expect(parseRuntimeConfig({ apiBaseUrl: 'https://x.example.com/api/' })).toEqual({
        apiBaseUrl: 'https://x.example.com/api',
      });
    });

    it.each([
      ['a scheme that is not a place to send a request', 'javascript:alert(1)'],
      ['a data URL', 'data:text/plain,hi'],
      ['a file URL', 'file:///etc/passwd'],
      ['a protocol-relative URL', '//evil.example.com'],
      ['a URL with a username', 'https://user@registry.example.com'],
      ['a URL with a password', 'https://user:secret@registry.example.com'],
      ['a URL with a query', 'https://registry.example.com/?x=1'],
      ['a URL with a fragment', 'https://registry.example.com/#x'],
      ['a bare host', 'registry.example.com'],
      ['a path with a space', '/a b'],
      ['a path with a query', '/a?b=1'],
      ['a number', 5062],
      ['null', null],
    ])('refuses %s', (_name, value) => {
      // The auth interceptor sends a bearer token to this origin. Anything that is not exactly
      // an HTTP(S) origin or a plain path is a value to refuse, not to interpret.
      expect(() => parseRuntimeConfig({ apiBaseUrl: value })).toThrow(RuntimeConfigError);
    });
  });

  describe('profile', () => {
    it.each(['self-hosted', 'cloud'])('takes %s', (profile) => {
      expect(parseRuntimeConfig({ profile })).toEqual({ profile });
    });

    it.each(['Cloud', 'saas', '', 7, null])('refuses %j', (profile) => {
      expect(() => parseRuntimeConfig({ profile })).toThrow(/profile must be one of/);
    });
  });

  describe('tenant', () => {
    it('is a name or null', () => {
      expect(parseRuntimeConfig({ tenant: 'acme' })).toEqual({ tenant: 'acme' });
      expect(parseRuntimeConfig({ tenant: null })).toEqual({ tenant: null });
    });

    it.each(['', '   ', 7, {}])('refuses %j', (tenant) => {
      expect(() => parseRuntimeConfig({ tenant })).toThrow(RuntimeConfigError);
    });
  });

  describe('defaultEnvironment', () => {
    it.each(['dev', 'prod-eu', 'team.a_1'])('takes %s', (defaultEnvironment) => {
      expect(parseRuntimeConfig({ defaultEnvironment })).toEqual({ defaultEnvironment });
    });

    it.each(['', 'has space', 'a/b', 'x'.repeat(65), 7])('refuses %j', (defaultEnvironment) => {
      expect(() => parseRuntimeConfig({ defaultEnvironment })).toThrow(RuntimeConfigError);
    });
  });
});

describe('fetchRuntimeConfig', () => {
  const answer = (body: string, init: ResponseInit & { type?: string } = {}) =>
    (async () =>
      new Response(body, {
        status: 200,
        headers: { 'content-type': 'application/json' },
        ...init,
      })) as unknown as typeof fetch;

  it('reads the file next to the document', async () => {
    let asked = '';
    const fetcher = (async (url: URL | string) => {
      asked = String(url);

      return new Response('{"apiBaseUrl":"/registry"}', {
        headers: { 'content-type': 'application/json' },
      });
    }) as unknown as typeof fetch;

    expect(await fetchRuntimeConfig(fetcher, 'http://localhost:5063/subjects/x')).toEqual({
      apiBaseUrl: '/registry',
    });
    // Relative to the document's base, so a deployment under a path prefix finds its own.
    expect(asked).toBe('http://localhost:5063/subjects/config.json');
  });

  it('asks without credentials and without a cache', async () => {
    let init: RequestInit | undefined;
    const fetcher = (async (_url: unknown, options?: RequestInit) => {
      init = options;

      return new Response('{}', { headers: { 'content-type': 'application/json' } });
    }) as unknown as typeof fetch;

    await fetchRuntimeConfig(fetcher, 'http://localhost/');

    expect(init).toMatchObject({ cache: 'no-store', credentials: 'omit' });
  });

  it('means the defaults when there is no file', async () => {
    expect(
      await fetchRuntimeConfig(
        (async () => new Response('', { status: 404 })) as unknown as typeof fetch,
        'http://localhost/',
      ),
    ).toEqual({});
  });

  it('means the defaults when the answer is a page, not a file', async () => {
    // What a development server says to any path it has never heard of: 200 and `index.html`.
    // It is not a configuration file however politely it answered.
    const page = answer('<!doctype html><title>Concordat</title>', {
      headers: { 'content-type': 'text/html' },
    });

    expect(await fetchRuntimeConfig(page, 'http://localhost/')).toEqual({});
  });

  it('means the defaults when the network failed', async () => {
    const down = (async () => {
      throw new TypeError('Failed to fetch');
    }) as unknown as typeof fetch;

    expect(await fetchRuntimeConfig(down, 'http://localhost/')).toEqual({});
  });

  it('stops, and says why, when the file is there and is not JSON', async () => {
    await expect(fetchRuntimeConfig(answer('{not json'), 'http://localhost/')).rejects.toThrow(
      /not valid JSON/,
    );
  });

  it('stops, and says why, when the file is there and is wrong', async () => {
    // Not the defaults: that would point a deployment someone configured at the wrong place
    // without saying so.
    await expect(
      fetchRuntimeConfig(answer('{"apiBaseUrl":"javascript:alert(1)"}'), 'http://localhost/'),
    ).rejects.toThrow(/apiBaseUrl/);
  });
});
