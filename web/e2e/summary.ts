/**
 * Talking to the registry about its overview, for `dashboard.spec.ts`.
 *
 * Kept out of `registry.ts` on purpose: that file is every spec's, and a helper only this spec
 * reads is not worth a merge conflict with every other change to it.
 */
const REGISTRY = process.env.CONCORDAT_REGISTRY ?? 'http://localhost:5062';

/** The part of `GET /v1/environments/{env}/summary` a spec asserts against. */
export interface SummaryView {
  readonly subjects: { total: number; active: number; deprecated: number; retired: number };
  readonly versions: {
    total: number;
    active: number;
    awaitingApproval: number;
    rejected: number;
    dismissed: number;
  };
  readonly newestSubjects: readonly { name: string; latest: number | null; versions: number }[];
  readonly breakingChanges: readonly {
    subject: string;
    ordinal: number;
    status: string;
    registeredBy: string;
  }[];
  readonly enforcement: readonly {
    mode: string;
    contracts: number;
    publishBindings: number;
    consumeBindings: number;
  }[];
  readonly violations: { distinct: number; recent: number; windowHours: number };
}

async function call(credential: string, path: string, init?: RequestInit): Promise<Response> {
  return fetch(`${REGISTRY}${path}`, {
    ...init,
    headers: {
      'content-type': 'application/json',
      authorization: `Bearer ${credential}`,
      ...(init?.headers ?? {}),
    },
  });
}

/** Reads the overview straight from the registry, so a spec can say what the screen must show. */
export async function readSummary(credential: string, environment: string): Promise<SummaryView> {
  const response = await call(credential, `/v1/environments/${environment}/summary`);

  if (!response.ok) {
    throw new Error(`Reading the summary of '${environment}' answered ${response.status}.`);
  }

  return (await response.json()) as SummaryView;
}

/** The enforcement row for one mode. */
export function tier(summary: SummaryView, mode: 'OFF' | 'MONITOR' | 'ENFORCE') {
  const found = summary.enforcement.find((row) => row.mode === mode);

  if (found === undefined) {
    throw new Error(`The summary carries no '${mode}' enforcement row.`);
  }

  return found;
}

/** Adds a publish binding through the API, to arrange a contract the dashboard then counts. */
export async function addPublishBindingRaw(
  credential: string,
  environment: string,
  contract: string,
  exchange: string,
  pattern: string,
  subjects: readonly { subject: string; selector: string }[],
): Promise<void> {
  const response = await call(
    credential,
    `/v1/environments/${environment}/contracts/${contract}/publishes`,
    { method: 'POST', body: JSON.stringify({ exchange, routingKeyPattern: pattern, subjects }) },
  );

  if (!response.ok) {
    throw new Error(`Adding a publish binding to '${contract}' answered ${response.status}.`);
  }
}

/**
 * Reports one violation, as an SDK would.
 *
 * A violation is permanent and keyed by side, route, subject and code, so the route is named for
 * the run: a fixed one would be a row the second run only increments, and the distinct count the
 * spec asserts would not move.
 */
export async function reportViolationRaw(
  credential: string,
  environment: string,
  route: string,
): Promise<void> {
  const response = await call(credential, `/v1/environments/${environment}/violations`, {
    method: 'POST',
    body: JSON.stringify({
      reportedBy: 'e2e-dashboard',
      violations: [
        {
          side: 'PUBLISH',
          route,
          subject: null,
          code: 'payload_invalid',
          detail: 'reported by dashboard.spec.ts',
          occurrences: 1,
        },
      ],
    }),
  });

  if (!response.ok) {
    throw new Error(`Reporting a violation answered ${response.status}.`);
  }
}
