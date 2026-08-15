import {
  ensureEnvironment,
  ensureHeldVersion,
  ensureOwner,
  ensureReader,
  ensureSubject,
} from './registry';

const REGISTRY = process.env.CONCORDAT_REGISTRY ?? 'http://localhost:5062';
const WEB = process.env.CONCORDAT_WEB_URL ?? 'http://localhost:4300';

/**
 * Checks both halves are up, then arranges the accounts every test needs.
 *
 * <b>The reachability check is separate from the arrangement, and says which one is missing.</b>
 * "connect ECONNREFUSED 127.0.0.1:4300" in the middle of a sign-in test is a message that sends
 * somebody looking at the sign-in code.
 */
export default async function globalSetup(): Promise<void> {
  await reachable(WEB, 'the web app', 'npm start -- --port 4300');
  await reachable(
    `${REGISTRY}/health/ready`,
    'the registry',
    'cd deploy/compose && CONCORDAT_IMAGE=concordat/api:local docker compose --profile registry up -d',
  );

  const owner = await ensureOwner();
  await ensureReader(owner);

  // The app's default environment. Without it the subject list renders a 404 and every
  // assertion about the page shape is really an assertion about an error state.
  await ensureEnvironment(owner, 'dev');

  // One real subject, so the list has a row. See ensureSubject for why an empty table would
  // make every assertion here vacuous.
  await ensureSubject(owner, 'dev', 'acme.e2e.OrderCreated');

  // And a second whose tip is held at the approval gate, so the screens that render a status
  // have a real `AWAITING_APPROVAL` to render rather than only the happy one. See
  // ensureHeldVersion: this is the shape of the defect that shipped once already.
  await ensureHeldVersion(owner, 'dev', 'acme.e2e.PaymentTaken');
}

async function reachable(url: string, what: string, howToStart: string): Promise<void> {
  try {
    const response = await fetch(url);

    if (!response.ok) {
      throw new Error(`${response.status}`);
    }
  } catch (cause) {
    throw new Error(
      `Cannot reach ${what} at ${url}. Start it with:\n\n  ${howToStart}\n\n` +
        'See web/e2e/README.md.',
      { cause },
    );
  }
}
