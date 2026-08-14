import { DOMAIN_CONCORDAT_CODES, type DomainConcordatCode } from './concordat-codes.generated';

/**
 * Codes that reach a client but are not in the domain catalogue.
 *
 * Two sources, and they are worth keeping apart from the generated list because neither can
 * be derived from `ConcordatCodes.cs`:
 *
 * - `invalid_request` is raised by the API's own request parsing — an unknown format token,
 *   a half-specified compatibility policy — before any domain rule runs.
 * - `registry_unreachable` and `registry_refused` are synthesised in this app when there was
 *   no usable response at all. The spellings are the CLI's (`RegistryApi.cs`) on purpose:
 *   an operator comparing a browser console with a CI log should see the same token for the
 *   same condition.
 *
 * `insufficient_scope` was here until M8 put it in `ConcordatCodes.cs`. It is now generated,
 * and the overlap test is what said so — the union would otherwise have carried it twice.
 */
export const TRANSPORT_CONCORDAT_CODES = [
  'invalid_request',
  'registry_unreachable',
  'registry_refused',
] as const;

/** A code raised outside the domain catalogue. */
export type TransportConcordatCode = (typeof TRANSPORT_CONCORDAT_CODES)[number];

/** Every code this build knows how to name. */
export type KnownConcordatCode = DomainConcordatCode | TransportConcordatCode;

/**
 * A `concordatCode` as it arrives on the wire.
 *
 * The `(string & {})` arm is deliberate. Narrowing this to `KnownConcordatCode` would be a
 * lie: a registry newer than this bundle can emit a code that was not in the catalogue when
 * the bundle was built, and a UI whose type system forbids that will either crash or fall
 * into a branch meant for something else. The union keeps completion and exhaustiveness
 * checks working for the codes we do know while forcing every consumer to have a default
 * arm — which, since the API always sends a human-readable `detail`, can simply show it.
 */
export type ConcordatCode = KnownConcordatCode | (string & {});

const KNOWN = new Set<string>([...DOMAIN_CONCORDAT_CODES, ...TRANSPORT_CONCORDAT_CODES]);

/** Whether this build has a name for the code. */
export function isKnownConcordatCode(code: string): code is KnownConcordatCode {
  return KNOWN.has(code);
}
