namespace Concordat.Domain.Results;

/// <summary>
/// Every <c>concordatCode</c> the domain can emit.
/// </summary>
/// <remarks>
/// These strings are normative protocol under ADR-019: they appear in RFC 9457 Problem
/// Details responses and clients branch on them. They live beside the rules that raise
/// them so the catalogue cannot drift from the behaviour it describes.
/// </remarks>
public static class ConcordatCodes
{
    /// <summary>A schema id was not 32 lowercase hexadecimal characters.</summary>
    public const string SchemaIdMalformed = "schema_id_malformed";

    /// <summary>A subject name did not match the canonical grammar.</summary>
    public const string SubjectNameInvalid = "subject_name_invalid";

    // ------------------------------------------------------------- environments (M7)

    /// <summary>An environment name did not match the canonical grammar.</summary>
    public const string EnvironmentNameInvalid = "environment_name_invalid";

    /// <summary>No environment with the given name exists.</summary>
    public const string EnvironmentNotFound = "environment_not_found";

    /// <summary>An environment with that name already exists.</summary>
    public const string EnvironmentAlreadyExists = "environment_already_exists";

    /// <summary>A broker URI was absent, not absolute, or not an AMQP 0-9-1 scheme.</summary>
    public const string BrokerUriInvalid = "broker_uri_invalid";

    /// <summary>
    /// A broker with that endpoint or display name is already registered in the environment.
    /// </summary>
    public const string BrokerAlreadyExists = "broker_already_exists";

    /// <summary>No broker with the given id exists in the environment.</summary>
    public const string BrokerNotFound = "broker_not_found";

    /// <summary>A broker credential was missing a username or a password.</summary>
    public const string CredentialInvalid = "credential_invalid";

    // ---------------------------------------------------------------- contracts (M7.3)

    /// <summary>A contract name was empty or too long.</summary>
    public const string ContractNameInvalid = "contract_name_invalid";

    /// <summary>No contract with the given name exists in the environment.</summary>
    public const string ContractNotFound = "contract_not_found";

    /// <summary>A contract with that name already exists in the environment.</summary>
    public const string ContractAlreadyExists = "contract_already_exists";

    /// <summary>A routing key pattern was not a valid AMQP topic pattern.</summary>
    public const string RoutingKeyPatternInvalid = "routing_key_pattern_invalid";

    /// <summary>A version selector was not <c>latest</c>, an ordinal, or <c>&gt;=N</c>.</summary>
    public const string VersionSelectorInvalid = "version_selector_invalid";

    /// <summary>
    /// Two bindings overlap and carry different subjects with no precedence to separate them.
    /// </summary>
    /// <remarks>
    /// Overlap is not textual: <c>orders.*</c> and <c>*.created</c> both match
    /// <c>orders.created</c>. Without this, which contract a publisher is judged against would
    /// depend on iteration order.
    /// </remarks>
    public const string BindingConflict = "binding_conflict";

    // --------------------------------------------------------------- governance (M7.4)

    /// <summary>A service name was empty, too long, or outside the permitted grammar.</summary>
    public const string ServiceNameInvalid = "service_name_invalid";

    /// <summary>No service with the given name is registered in the environment.</summary>
    public const string ServiceNotFound = "service_not_found";

    /// <summary>An audit query carried a filter value that could not be understood.</summary>
    public const string AuditFilterInvalid = "audit_filter_invalid";

    /// <summary>A promotion named a target environment that is the source environment.</summary>
    public const string PromotionTargetInvalid = "promotion_target_invalid";

    /// <summary>
    /// A promotion named a source version that is not active — a proposal, a rejection or a
    /// dismissal.
    /// </summary>
    /// <remarks>
    /// Promotion moves something the source environment has already accepted. Promoting a
    /// version still awaiting approval would launder it into the target, where it would be
    /// judged only against the target's history and never against the review it was waiting
    /// for.
    /// </remarks>
    public const string PromotionSourceNotActive = "promotion_source_not_active";

    // ------------------------------------------------------------ notifications (M7.5)

    /// <summary>A subscription endpoint was not a usable address or https URL.</summary>
    public const string SubscriptionEndpointInvalid = "subscription_endpoint_invalid";

    /// <summary>A subscription named a channel or event token that is not known.</summary>
    public const string SubscriptionInvalid = "subscription_invalid";

    /// <summary>No subscription with the given id exists in the environment.</summary>
    public const string SubscriptionNotFound = "subscription_not_found";

    // ---------------------------------------------------------------- identity (M8)

    /// <summary>A scope token is not one this build knows.</summary>
    public const string ScopeInvalid = "scope_invalid";

    /// <summary>A role token is not one this build knows.</summary>
    public const string RoleInvalid = "role_invalid";

    /// <summary>An email address was empty, too long, or not an address.</summary>
    public const string EmailInvalid = "email_invalid";

    /// <summary>A password did not meet the minimum requirements.</summary>
    public const string PasswordInvalid = "password_invalid";

    /// <summary>No user with the given identity exists.</summary>
    public const string UserNotFound = "user_not_found";

    /// <summary>A user with that email address already exists.</summary>
    public const string UserAlreadyExists = "user_already_exists";

    /// <summary>No API key with the given id exists.</summary>
    public const string ApiKeyNotFound = "api_key_not_found";

    /// <summary>An API key label was empty or too long.</summary>
    public const string ApiKeyLabelInvalid = "api_key_label_invalid";

    /// <summary>
    /// The credential was missing, malformed, expired or revoked.
    /// </summary>
    /// <remarks>
    /// Deliberately one code for all four. Distinguishing "no such key" from "revoked key"
    /// tells an attacker which of their guesses was once real.
    /// </remarks>
    public const string Unauthenticated = "unauthenticated";

    /// <summary>
    /// The caller is known but holds none of the scopes this endpoint requires (ADR-018).
    /// </summary>
    public const string InsufficientScope = "insufficient_scope";

    /// <summary>An actor identifier was empty or too long.</summary>
    public const string ActorIdInvalid = "actor_id_invalid";

    /// <summary>A schema reference was malformed.</summary>
    public const string ReferenceInvalid = "reference_invalid";

    /// <summary>Two references in one schema shared a name.</summary>
    public const string DuplicateReferenceName = "duplicate_reference_name";

    /// <summary>The reference graph contains a cycle.</summary>
    public const string ReferenceCycle = "reference_cycle";

    /// <summary>A schema body was empty or whitespace.</summary>
    public const string SchemaBodyEmpty = "schema_body_empty";

    /// <summary>A schema body exceeded the documented size ceiling.</summary>
    public const string SchemaTooLarge = "schema_too_large";

    /// <summary>A schema body was not well-formed in its declared format.</summary>
    public const string SchemaMalformed = "schema_malformed";

    /// <summary>
    /// The schema depends on a definition outside itself, which v1 does not support for this
    /// format (ADR-023).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ReferenceInvalid"/>, which means a reference was malformed. This
    /// one means the reference was well-formed and Concordat is declining to resolve it: Avro
    /// fullnames and Protobuf <c>import</c>s have nowhere to pin a version, so following them
    /// would silently bind to whatever the target happens to be at read time.
    /// </remarks>
    public const string SchemaReferencesUnsupported = "schema_references_unsupported";

    /// <summary>
    /// The schema declares a JSON Schema dialect Concordat does not implement (M6.1).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SchemaMalformed"/>: the document is well-formed, and the
    /// registry is declining to interpret it under rules it was not written against. Keywords
    /// changed meaning between drafts — <c>items</c> most visibly — so guessing is worse than
    /// refusing.
    /// </remarks>
    public const string SchemaDialectUnsupported = "schema_dialect_unsupported";

    /// <summary>A semantic version label was not <c>MAJOR.MINOR.PATCH</c>.</summary>
    public const string SemverInvalid = "semver_invalid";

    /// <summary>Pre-release and build metadata are not supported in v1.</summary>
    public const string SemverPrereleaseUnsupported = "semver_prerelease_unsupported";

    /// <summary>A semantic version label did not increase on the previous label.</summary>
    public const string SemverNotIncreasing = "semver_not_increasing";

    /// <summary>
    /// A breaking change was labelled MINOR or PATCH. ADR-004: the label is verified.
    /// </summary>
    public const string SemverLabelUnderstatesBreakage = "semver_label_understates_breakage";

    /// <summary>A compatibility verdict was evaluated against a different policy than the subject's.</summary>
    public const string VerdictPolicyMismatch = "verdict_policy_mismatch";

    /// <summary>The first version of a subject cannot break anything, but was reported breaking.</summary>
    public const string FirstVersionCannotBreak = "first_version_cannot_break";

    /// <summary>A version's format did not match the subject's format.</summary>
    public const string FormatMismatch = "format_mismatch";

    /// <summary>The subject is retired and accepts no further versions.</summary>
    public const string SubjectRetired = "subject_retired";

    /// <summary>The requested lifecycle transition is not allowed.</summary>
    public const string LifecycleTransitionInvalid = "lifecycle_transition_invalid";

    /// <summary>No version with the given ordinal exists on the subject.</summary>
    public const string VersionNotFound = "version_not_found";

    /// <summary>No subject with the given name exists in the environment.</summary>
    public const string SubjectNotFound = "subject_not_found";

    /// <summary>A subject with that name already exists in the environment.</summary>
    public const string SubjectAlreadyExists = "subject_already_exists";

    /// <summary>No schema with the given id is visible to the caller.</summary>
    public const string SchemaNotFound = "schema_not_found";

    // ---------------------------------------------------------------- envelope
    //
    // Read-side codes (ADR-010). Split deliberately between codes that REJECT a message and
    // codes that only WARN: quarantining a structurally valid payload because someone
    // mistyped a semantic version label would be a self-inflicted outage.

    /// <summary>The envelope version header is present but empty or unparseable. Rejects.</summary>
    public const string EnvelopeMalformed = "envelope_malformed";

    /// <summary>
    /// The envelope declares a version this client does not implement. Rejects, and the
    /// reader must not interpret any other <c>concordat-*</c> header: a later version may
    /// have redefined them.
    /// </summary>
    public const string EnvelopeVersionUnsupported = "envelope_version_unsupported";

    /// <summary>The envelope is present but carries no schema id. Rejects.</summary>
    public const string EnvelopeSchemaIdMissing = "envelope_schema_id_missing";

    /// <summary>
    /// A header value was neither a string nor a byte array. Rejects rather than calling
    /// <c>ToString()</c>, which would invent a plausible-looking wrong value.
    /// </summary>
    public const string EnvelopeHeaderTypeInvalid = "envelope_header_type_invalid";

    /// <summary>
    /// A header value was not well-formed UTF-8. Rejects — decoding leniently would
    /// substitute U+FFFD and turn a corrupt id into a valid-looking wrong one.
    /// </summary>
    public const string EnvelopeHeaderEncodingInvalid = "envelope_header_encoding_invalid";

    /// <summary>The subject could not be determined from the envelope, properties or registry. Rejects.</summary>
    public const string EnvelopeSubjectUnresolvable = "envelope_subject_unresolvable";

    /// <summary>The declared format disagrees with the format the registry holds for that id. Rejects.</summary>
    public const string EnvelopeFormatMismatch = "envelope_format_mismatch";

    /// <summary>The declared format is not a known token. Rejects.</summary>
    public const string EnvelopeFormatUnknown = "envelope_format_unknown";

    /// <summary>
    /// <c>concordat-subject</c> and <c>properties.type</c> disagree. Warns; the header wins.
    /// </summary>
    public const string EnvelopeSubjectTypeMismatch = "envelope_subject_type_mismatch";

    /// <summary>
    /// The version ordinal was unparseable. Warns only — the schema id already pins the schema.
    /// </summary>
    public const string EnvelopeOrdinalMalformed = "envelope_ordinal_malformed";

    /// <summary>The payload did not validate against its declared schema.</summary>
    public const string PayloadInvalid = "payload_invalid";

    /// <summary>The version is not awaiting approval, so it cannot be approved or rejected.</summary>
    public const string VersionNotAwaitingApproval = "version_not_awaiting_approval";

    /// <summary>A changelog exceeded the permitted length.</summary>
    public const string ChangelogTooLong = "changelog_too_long";

    /// <summary>
    /// A client could not resolve a schema, so the operation could not be enforced.
    /// </summary>
    /// <remarks>
    /// Raised by clients rather than the registry, but catalogued here because ADR-019 makes
    /// these strings normative for every SDK. A Go consumer and a .NET consumer must report the
    /// same condition with the same token, or an operator cannot alert across the fleet.
    /// </remarks>
    public const string SchemaUnresolvable = "schema_unresolvable";
}
