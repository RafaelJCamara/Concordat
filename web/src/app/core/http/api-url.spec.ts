import { describe, expect, it } from 'vitest';
import type { ConcordatConfig } from '../config/app-config';
import { apiRoot, environmentRoot, isApiUrl } from './api-url';

// `isApiUrl` is the gate that stops the auth interceptor handing a registry bearer token to
// a CDN. It is one boolean, and every way it can be too generous is a credential leak, so
// the cases below are mostly about what must *not* match.

const selfHosted: ConcordatConfig = {
  apiBaseUrl: '',
  profile: 'self-hosted',
  tenant: null,
  defaultEnvironment: 'dev',
};

const cloud: ConcordatConfig = {
  apiBaseUrl: 'https://registry.example.com',
  profile: 'cloud',
  tenant: 'acme',
  defaultEnvironment: 'dev',
};

/** Where the SPA itself is served from, i.e. `document.baseURI`. */
const APP = 'https://app.example.com/';

describe('apiRoot', () => {
  it('is root-relative when the API shares the origin', () => {
    // The self-hosted default. A relative root means there is no cross-origin request for a
    // credential to be attached to, which is the point of the empty `apiBaseUrl`.
    expect(apiRoot(selfHosted)).toBe('/v1');
  });

  it('carries the configured origin when the API is elsewhere', () => {
    expect(apiRoot(cloud)).toBe('https://registry.example.com/v1');
  });
});

describe('environmentRoot', () => {
  it('puts the version prefix before the environment', () => {
    expect(environmentRoot(selfHosted, 'dev')).toBe('/v1/environments/dev');
  });

  it('percent-encodes the environment name', () => {
    // An environment name is user-chosen. Without encoding, one containing a slash would
    // silently address a different route rather than fail.
    expect(environmentRoot(selfHosted, 'eu/prod')).toBe('/v1/environments/eu%2Fprod');
  });
});

describe('isApiUrl', () => {
  it('recognises a request under the versioned root', () => {
    expect(isApiUrl('/v1/environments/dev/subjects', selfHosted, APP)).toBe(true);
  });

  it('recognises the root itself', () => {
    expect(isApiUrl('/v1', selfHosted, APP)).toBe(true);
  });

  it('does not treat a longer first segment as a match', () => {
    // The reason the check is a path-segment comparison and not `startsWith`. A registry
    // that later serves `/v1x` — or anything else beginning with the same characters —
    // would otherwise receive the credential meant for `/v1`.
    expect(isApiUrl('/v1x/subjects', selfHosted, APP)).toBe(false);
  });

  it('refuses a same-path URL on another origin', () => {
    // The failure this whole function exists to prevent: an absolute URL to somewhere else
    // that happens to have the same shape.
    expect(isApiUrl('https://cdn.example.com/v1/subjects', selfHosted, APP)).toBe(false);
  });

  it('accepts an absolute URL that resolves back to the app origin', () => {
    expect(isApiUrl('https://app.example.com/v1/subjects', selfHosted, APP)).toBe(true);
  });

  it('resolves a relative URL the way the browser will', () => {
    expect(isApiUrl('v1/subjects', selfHosted, APP)).toBe(true);
  });

  it('refuses a path outside the versioned root', () => {
    // A doc link or an asset served by the same origin is still not the API.
    expect(isApiUrl('/assets/logo.svg', selfHosted, APP)).toBe(false);
  });

  it('refuses an unparseable URL rather than assuming', () => {
    expect(isApiUrl('http://', selfHosted, APP)).toBe(false);
  });

  describe('when the API is on its own origin', () => {
    it('accepts the configured origin', () => {
      expect(isApiUrl('https://registry.example.com/v1/subjects', cloud, APP)).toBe(true);
    });

    it('refuses the app origin', () => {
      // With a cloud config, `/v1/...` is the SPA's own host, not the registry. Matching on
      // path alone would attach the credential to whatever serves the bundle.
      expect(isApiUrl('/v1/subjects', cloud, APP)).toBe(false);
    });
  });
});
