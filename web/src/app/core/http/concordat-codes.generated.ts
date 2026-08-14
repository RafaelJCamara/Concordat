// GENERATED FILE — do not edit.
//
// Source:    src/core/Concordat.Domain/Results/ConcordatCodes.cs
// Regenerate: npm run codes:generate   (npm run codes:check fails the build on drift)
//
// The catalogue of stable error codes the domain can emit (ADR-019). These are protocol,
// not diagnostics: they arrive as the `concordatCode` member of an RFC 9457 Problem
// Details body and callers branch on them.

/** Every `concordatCode` the domain can emit, in catalogue order. */
export const DOMAIN_CONCORDAT_CODES = [
  'schema_id_malformed',
  'subject_name_invalid',
  'environment_name_invalid',
  'environment_not_found',
  'environment_already_exists',
  'broker_uri_invalid',
  'broker_already_exists',
  'broker_not_found',
  'credential_invalid',
  'actor_id_invalid',
  'reference_invalid',
  'duplicate_reference_name',
  'reference_cycle',
  'schema_body_empty',
  'schema_too_large',
  'schema_malformed',
  'schema_references_unsupported',
  'schema_dialect_unsupported',
  'semver_invalid',
  'semver_prerelease_unsupported',
  'semver_not_increasing',
  'semver_label_understates_breakage',
  'verdict_policy_mismatch',
  'first_version_cannot_break',
  'format_mismatch',
  'subject_retired',
  'lifecycle_transition_invalid',
  'version_not_found',
  'subject_not_found',
  'subject_already_exists',
  'schema_not_found',
  'envelope_malformed',
  'envelope_version_unsupported',
  'envelope_schema_id_missing',
  'envelope_header_type_invalid',
  'envelope_header_encoding_invalid',
  'envelope_subject_unresolvable',
  'envelope_format_mismatch',
  'envelope_format_unknown',
  'envelope_subject_type_mismatch',
  'envelope_ordinal_malformed',
  'payload_invalid',
  'version_not_awaiting_approval',
  'changelog_too_long',
  'schema_unresolvable',
] as const;

/** A code from the domain catalogue. */
export type DomainConcordatCode = (typeof DOMAIN_CONCORDAT_CODES)[number];
