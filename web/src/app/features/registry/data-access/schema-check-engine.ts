import Ajv2020, { type ErrorObject } from 'ajv/dist/2020';
import { SUPPORTED_DIALECTS } from '../../../domain/registry/schema-document';
import type {
  AdvisoryFinding,
  PayloadCheck,
  PayloadError,
  SchemaCheckRequest,
  SchemaCheckResult,
} from '../../../domain/registry/schema-check';

/** How many sample-message errors are listed before the rest are only counted. */
const MAX_PAYLOAD_ERRORS = 25;

/**
 * Judges a JSON Schema and a sample message with `ajv`.
 *
 * <b>Runs in a worker, not in the page, for two reasons that are both about what `ajv` is.</b>
 *
 * - It compiles each schema to JavaScript with `new Function`. The page's policy is
 *   `script-src 'self'` with no `unsafe-eval`, deliberately — it is the backstop against a
 *   future template mistake — and adding `unsafe-eval` to the page to host a validator would
 *   spend that backstop for a convenience. A worker is a separate context with no DOM, and the
 *   policy that governs it is the one on its own script, so the eval it needs stays inside
 *   something that can be given no network and no page to reach.
 * - It compiles `pattern` as a regular expression, and a pathological one can run for as long as
 *   it likes. On the page's thread that is a frozen tab; in a worker it is something a timeout
 *   can terminate.
 *
 * <b>Never stricter or looser than the registry — by being advice.</b> The registry is draft
 * 2020-12 only and refuses other dialects (`schema_dialect_unsupported`), so this refuses to
 * guess at them: a document declaring another dialect is not handed to `ajv` at all, which
 * would otherwise either throw or quietly apply 2020-12 rules to a draft-07 document. Beyond
 * that `ajv` is run permissively (`strict: false`, formats off), because its strict mode flags
 * things the registry accepts, and what it still finds is reported as advice the page never
 * blocks on.
 *
 * No Angular and no DOM here: this file is imported by the worker and by plain Node tests.
 *
 * `ajv` is CommonJS, which the build warns about on every run; `angular.json` lists it under
 * `allowedCommonJsDependencies` so that the one warning that would otherwise always be there
 * does not teach everybody to stop reading the others. The cost it warns of — no tree-shaking —
 * is paid inside a lazy worker nobody downloads until the first Validate. The dev server is told
 * to bundle it (`prebundle.exclude`) because a worker's imports are otherwise discovered at
 * runtime, and Vite answers a late discovery by reloading the page, which in a test is a typed
 * schema vanishing mid-assertion.
 */
export function checkSchema(request: SchemaCheckRequest): SchemaCheckResult {
  const checked = request.schema;
  const unavailable = (reason: string): SchemaCheckResult => ({
    checked,
    skipped: reason,
    advice: [],
    payload: request.payload === null ? null : { kind: 'unavailable', reason },
  });

  let schema: unknown;

  try {
    schema = JSON.parse(request.schema);
  } catch {
    // The page has already said so, with a position. Saying it again, differently, only makes
    // the real message harder to find.
    return unavailable('The schema is not well-formed JSON yet.');
  }

  if (typeof schema !== 'boolean' && (typeof schema !== 'object' || schema === null)) {
    return unavailable('A JSON Schema is an object or a boolean.');
  }

  const declared =
    typeof schema === 'object' ? (schema as Record<string, unknown>)['$schema'] : null;

  if (typeof declared === 'string' && !SUPPORTED_DIALECTS.includes(declared)) {
    return unavailable(
      `Dialect '${declared}' is not 2020-12, which is all the registry implements.`,
    );
  }

  const ajv = new Ajv2020({
    // `strict` flags unknown keywords, unknown formats and ambiguous types, all of which the
    // registry accepts; a check stricter than the registry is the failure this file avoids.
    strict: false,
    allErrors: true,
    // `format` is checked by the registry's engine, with its own list. Pretending to agree
    // about which strings are a `date-time` would be the one place this is confidently wrong.
    validateFormats: false,
    validateSchema: false,
    logger: false,
  });

  const advice = adviseOnSchema(ajv, schema);
  const payload = request.payload === null ? null : checkPayload(ajv, schema, request.payload);

  return { checked, skipped: null, advice, payload };
}

/** Whether the schema is valid under the 2020-12 metaschema, and what is not if it is not. */
function adviseOnSchema(ajv: Ajv2020, schema: unknown): readonly AdvisoryFinding[] {
  const valid = ajv.validateSchema(schema as object | boolean);

  if (valid === true || ajv.errors == null) {
    return [];
  }

  return summarise(ajv.errors).map((error) => ({
    code: 'ajv_metaschema',
    pointer: error.instancePath,
    message: describe(error),
  }));
}

/**
 * One finding per location, and the most useful one.
 *
 * The metaschema is built from `anyOf`, `$ref` and `$dynamicRef`, so a single wrong `type`
 * comes back as three errors — the one that names the allowed values, one saying "must be an
 * array", and one saying "must match a schema in anyOf". Listing all three is noise, and the
 * last is worse than noise: it says nothing about what to change.
 */
function summarise(errors: readonly ErrorObject[]): readonly ErrorObject[] {
  const structural = new Set(['anyOf', 'oneOf', 'allOf', 'if', 'not', '$ref', '$dynamicRef']);
  const byLocation = new Map<string, ErrorObject>();

  for (const error of errors) {
    if (structural.has(error.keyword) || byLocation.has(error.instancePath)) {
      continue;
    }

    byLocation.set(error.instancePath, error);
  }

  return [...byLocation.values()];
}

function describe(error: ErrorObject): string {
  const where = error.instancePath === '' ? 'The schema' : error.instancePath;
  const allowed = (error.params as { allowedValues?: readonly unknown[] }).allowedValues;

  // "must be equal to one of the allowed values" followed by the values is the same sentence
  // twice; the values are the part that says what to change.
  if (error.keyword === 'enum' && allowed !== undefined) {
    return `${where} must be one of ${allowed.map((v) => JSON.stringify(v)).join(', ')}.`;
  }

  return `${where} ${error.message ?? 'is not valid'}.`;
}

function checkPayload(ajv: Ajv2020, schema: unknown, payload: string): PayloadCheck {
  let message: unknown;

  try {
    message = JSON.parse(payload);
  } catch (error) {
    return {
      kind: 'not_json',
      message: error instanceof Error ? error.message : 'The sample is not well-formed JSON.',
    };
  }

  let validate: ReturnType<Ajv2020['compile']>;

  try {
    validate = ajv.compile(schema as object | boolean);
  } catch (error) {
    return { kind: 'unavailable', reason: explainCompileFailure(error) };
  }

  if (validate(message)) {
    return { kind: 'valid' };
  }

  const errors: PayloadError[] = (validate.errors ?? []).map((e) => ({
    path: e.instancePath,
    message: e.message ?? 'is not valid',
  }));

  return {
    kind: 'invalid',
    errors: errors.slice(0, MAX_PAYLOAD_ERRORS),
    omitted: Math.max(0, errors.length - MAX_PAYLOAD_ERRORS),
  };
}

/**
 * Why a schema could not be compiled, in words about the schema.
 *
 * A reference `ajv` cannot follow is the ordinary case and not a defect in the schema:
 * `concordat://` references are resolved by the registry from the version's reference list, and
 * this has no registry to ask. It has to be said as "could not be judged here", never as
 * "valid" and never as "invalid".
 */
function explainCompileFailure(error: unknown): string {
  const missing = (error as { missingRef?: unknown } | null)?.missingRef;

  if (typeof missing === 'string') {
    return (
      `The schema refers to '${missing}', which only the registry can resolve, so a sample ` +
      'cannot be checked against it here.'
    );
  }

  return error instanceof Error
    ? `The schema does not compile: ${error.message}`
    : 'The schema does not compile.';
}
