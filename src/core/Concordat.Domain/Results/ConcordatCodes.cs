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

    /// <summary>The version is not awaiting approval, so it cannot be approved or rejected.</summary>
    public const string VersionNotAwaitingApproval = "version_not_awaiting_approval";

    /// <summary>A changelog exceeded the permitted length.</summary>
    public const string ChangelogTooLong = "changelog_too_long";
}
