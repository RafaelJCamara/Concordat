<!-- GENERATED FILE — do not edit by hand. -->
<!-- Source: src/core/Concordat.Domain/Results/ConcordatCodes.cs -->
<!-- Regenerate: node scripts/generate-protocol-docs.mjs -->

# `concordatCode` catalogue

**Normative.** One of the five artifacts ADR-019 names as the protocol.

Every failure the registry reports carries one of these in the `concordatCode` member of an
RFC 9457 Problem Details body. **They are the strings clients branch on**, so they are stable:
renaming one is a breaking protocol change, not a refactor. They are deliberately not derived
from any implementation's enum or class names — a language that spells its members differently
must still emit exactly these.

Codes are also used where no HTTP response is involved: the client and middleware raise some of
them locally, so that a Go consumer and a .NET consumer report the same condition with the same
token and an operator can alert across a fleet.

> This file is generated from `src/core/Concordat.Domain/Results/ConcordatCodes.cs` and checked in CI. If it disagrees with the
> registry, the registry is right and this file is stale — please open an issue.

## Codes

| Code | Meaning |
| --- | --- |
| `schema_id_malformed` | A schema id was not 32 lowercase hexadecimal characters. |
| `subject_name_invalid` | A subject name did not match the canonical grammar. |
| `environment_name_invalid` | An environment name did not match the canonical grammar. |
| `environment_not_found` | No environment with the given name exists. |
| `environment_already_exists` | An environment with that name already exists. |
| `broker_uri_invalid` | A broker URI was absent, not absolute, or not an AMQP 0-9-1 scheme. |
| `broker_already_exists` | A broker with that endpoint or display name is already registered in the environment. |
| `broker_not_found` | No broker with the given id exists in the environment. |
| `credential_invalid` | A broker credential was missing a username or a password. |
| `actor_id_invalid` | An actor identifier was empty or too long. |
| `reference_invalid` | A schema reference was malformed. |
| `duplicate_reference_name` | Two references in one schema shared a name. |
| `reference_cycle` | The reference graph contains a cycle. |
| `schema_body_empty` | A schema body was empty or whitespace. |
| `schema_too_large` | A schema body exceeded the documented size ceiling. |
| `schema_malformed` | A schema body was not well-formed in its declared format. |
| `schema_references_unsupported` | The schema depends on a definition outside itself, which v1 does not support for this format (ADR-023). |
| `schema_dialect_unsupported` | The schema declares a JSON Schema dialect Concordat does not implement (M6.1). |
| `semver_invalid` | A semantic version label was not `MAJOR.MINOR.PATCH`. |
| `semver_prerelease_unsupported` | Pre-release and build metadata are not supported in v1. |
| `semver_not_increasing` | A semantic version label did not increase on the previous label. |
| `semver_label_understates_breakage` | A breaking change was labelled MINOR or PATCH. ADR-004: the label is verified. |
| `verdict_policy_mismatch` | A compatibility verdict was evaluated against a different policy than the subject's. |
| `first_version_cannot_break` | The first version of a subject cannot break anything, but was reported breaking. |
| `format_mismatch` | A version's format did not match the subject's format. |
| `subject_retired` | The subject is retired and accepts no further versions. |
| `lifecycle_transition_invalid` | The requested lifecycle transition is not allowed. |
| `version_not_found` | No version with the given ordinal exists on the subject. |
| `subject_not_found` | No subject with the given name exists in the environment. |
| `subject_already_exists` | A subject with that name already exists in the environment. |
| `schema_not_found` | No schema with the given id is visible to the caller. |
| `envelope_malformed` | The envelope version header is present but empty or unparseable. Rejects. |
| `envelope_version_unsupported` | The envelope declares a version this client does not implement. Rejects, and the reader must not interpret any other `concordat-*` header: a later version may have redefined them. |
| `envelope_schema_id_missing` | The envelope is present but carries no schema id. Rejects. |
| `envelope_header_type_invalid` | A header value was neither a string nor a byte array. Rejects rather than calling `ToString()`, which would invent a plausible-looking wrong value. |
| `envelope_header_encoding_invalid` | A header value was not well-formed UTF-8. Rejects — decoding leniently would substitute U+FFFD and turn a corrupt id into a valid-looking wrong one. |
| `envelope_subject_unresolvable` | The subject could not be determined from the envelope, properties or registry. Rejects. |
| `envelope_format_mismatch` | The declared format disagrees with the format the registry holds for that id. Rejects. |
| `envelope_format_unknown` | The declared format is not a known token. Rejects. |
| `envelope_subject_type_mismatch` | `concordat-subject` and `properties.type` disagree. Warns; the header wins. |
| `envelope_ordinal_malformed` | The version ordinal was unparseable. Warns only — the schema id already pins the schema. |
| `payload_invalid` | The payload did not validate against its declared schema. |
| `version_not_awaiting_approval` | The version is not awaiting approval, so it cannot be approved or rejected. |
| `changelog_too_long` | A changelog exceeded the permitted length. |
| `schema_unresolvable` | A client could not resolve a schema, so the operation could not be enforced. |

**45 codes.**

## Rules for implementers

- **Branch on the code, never on the HTTP status.** Several codes share a status, and the
  status alone does not tell you whether a retry could succeed. The split that matters most is
  a contract violation against an unreachable registry — see the CLI's exit codes, where they
  are 1 and 3 precisely so a pipeline cannot confuse them.
- **Treat an unknown code as a failure you cannot interpret**, not as a generic error to
  swallow. New codes are additive, and an SDK that silently maps everything it does not
  recognise onto one bucket hides exactly the new condition its users need to see.
- **Do not invent codes.** If your SDK needs to report something the catalogue has no word
  for, that belongs in the catalogue, because the whole point is that every language reports
  the same condition identically.
