import type { ConcordatConfig, DeploymentProfile } from './app-config';

/**
 * Configuration the deployment supplies, read at startup from `config.json`.
 *
 * <b>Why a file next to the bundle and not a build-time constant.</b> One image is served by
 * every deployment (see `ConcordatConfig`), so where the API lives cannot be compiled in: a
 * rebuild per deployment is how a self-hosted user ends up pointed at somebody else's registry.
 * The container writes this file from its environment when it starts (`docker/web/`), and a
 * static host can simply replace the one beside `index.html`.
 *
 * <b>The bundle ships `public/config.json`, and it is `{}`.</b> That says "the defaults", which
 * are the same-origin self-hosted shape development wants, and it exists so that there is always a
 * file to fetch: without one, `ng serve` answers 404 — its single-page fallback only covers paths
 * with no extension — and the browser logs a failed request as a console error on every page load
 * of every developer's machine. The container serves its generated file in its place.
 *
 * <b>Absent is fine; wrong is not.</b> A missing file means "use the defaults", but a file that
 * is there and cannot be understood stops the app with a message. The alternative — shrug, use
 * the defaults — would point a deployment someone had deliberately configured at the wrong
 * place without saying so, which is a worse failure than a page that explains itself.
 */
export type RuntimeConfig = Partial<ConcordatConfig>;

/** A `config.json` that is present and not usable. Carries what to tell the person. */
export class RuntimeConfigError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'RuntimeConfigError';
  }
}

const PROFILES: readonly DeploymentProfile[] = ['self-hosted', 'cloud'];

/** An environment slug, which the app puts in URLs. */
const ENVIRONMENT = /^[A-Za-z0-9._-]{1,64}$/;

/** A same-origin path prefix: rooted, not protocol-relative, nothing a URL would need escaped. */
const PATH_PREFIX = /^\/(?!\/)[A-Za-z0-9._~\-/]*$/;

/**
 * Reads and checks a parsed `config.json`.
 *
 * Every field is optional and every field is checked, because this file is the one place a
 * string from outside the bundle decides where credentials are sent: the auth interceptor
 * attaches a bearer token to requests to `apiBaseUrl`, so a value that is not exactly an HTTP(S)
 * origin or a same-origin path is refused here and not interpreted generously.
 * Unknown members are ignored, so a newer deployment's file does not break an older bundle.
 */
export function parseRuntimeConfig(raw: unknown): RuntimeConfig {
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) {
    throw new RuntimeConfigError('config.json must be a JSON object.');
  }

  const source = raw as Record<string, unknown>;
  const config: { -readonly [K in keyof RuntimeConfig]: RuntimeConfig[K] } = {};

  if ('apiBaseUrl' in source) {
    config.apiBaseUrl = parseApiBaseUrl(source['apiBaseUrl']);
  }

  if ('profile' in source) {
    const profile = source['profile'];

    if (typeof profile !== 'string' || !PROFILES.includes(profile as DeploymentProfile)) {
      throw new RuntimeConfigError(
        `config.json: profile must be one of ${PROFILES.join(', ')}; got ${JSON.stringify(profile)}.`,
      );
    }

    config.profile = profile as DeploymentProfile;
  }

  if ('tenant' in source) {
    const tenant = source['tenant'];

    if (tenant !== null && (typeof tenant !== 'string' || tenant.trim() === '')) {
      throw new RuntimeConfigError('config.json: tenant must be a non-empty string, or null.');
    }

    config.tenant = tenant;
  }

  if ('defaultEnvironment' in source) {
    const environment = source['defaultEnvironment'];

    if (typeof environment !== 'string' || !ENVIRONMENT.test(environment)) {
      throw new RuntimeConfigError(
        'config.json: defaultEnvironment must be 1-64 letters, digits, dots, underscores or hyphens.',
      );
    }

    config.defaultEnvironment = environment;
  }

  return config;
}

function parseApiBaseUrl(value: unknown): string {
  if (typeof value !== 'string') {
    throw new RuntimeConfigError('config.json: apiBaseUrl must be a string.');
  }

  // Empty is the self-hosted default: same origin, no CORS, and no credential that could be
  // sent anywhere else.
  if (value === '') {
    return '';
  }

  if (value.startsWith('/')) {
    const trimmed = value.replace(/\/+$/, '');

    if (!PATH_PREFIX.test(trimmed)) {
      throw new RuntimeConfigError(
        `config.json: apiBaseUrl '${value}' is not a plain path such as '/registry'.`,
      );
    }

    return trimmed;
  }

  let url: URL;

  try {
    url = new URL(value);
  } catch {
    throw new RuntimeConfigError(
      `config.json: apiBaseUrl '${value}' is neither empty, a path, nor an http(s) URL.`,
    );
  }

  // `javascript:`, `data:` and `file:` parse as URLs and are not places to send a request. A URL
  // carrying a username, a query or a fragment is not an API root, and a username in particular
  // is a credential that would be logged with every request.
  if (
    (url.protocol !== 'http:' && url.protocol !== 'https:') ||
    url.username !== '' ||
    url.password !== '' ||
    url.search !== '' ||
    url.hash !== ''
  ) {
    throw new RuntimeConfigError(
      `config.json: apiBaseUrl '${value}' must be a plain http(s) origin, optionally with a path.`,
    );
  }

  return `${url.origin}${url.pathname.replace(/\/+$/, '')}`;
}

/**
 * Fetches the deployment's configuration.
 *
 * Absent in three ways, all of which mean "defaults": a 404 (a static host that dropped the file
 * from its deploy), a network failure (a same-origin file that cannot be fetched means the app
 * itself is barely loading, and failing differently would hide that), and an HTML answer — which is
 * what a host with a catch-all route returns for a file it does not have, and is not a
 * configuration file however politely it said 200.
 */
export async function fetchRuntimeConfig(
  fetcher: typeof fetch = fetch,
  base: string = document.baseURI,
): Promise<RuntimeConfig> {
  let response: Response;

  try {
    response = await fetcher(new URL('config.json', base), {
      cache: 'no-store',
      // Not a request that has any business carrying a session.
      credentials: 'omit',
    });
  } catch {
    return {};
  }

  if (!response.ok || !(response.headers.get('content-type') ?? '').includes('json')) {
    return {};
  }

  let body: unknown;

  try {
    body = await response.json();
  } catch {
    throw new RuntimeConfigError('config.json is present but is not valid JSON.');
  }

  return parseRuntimeConfig(body);
}
