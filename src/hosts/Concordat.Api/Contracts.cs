using Concordat.Application.Registry;
using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;

namespace Concordat.Api;

/// <summary>Request to create a subject.</summary>
/// <param name="Name">The subject name.</param>
/// <param name="Format">One of <c>json</c>, <c>avro</c>, <c>protobuf</c>.</param>
/// <param name="Owner">Who owns the contract.</param>
/// <param name="CompatibilityMode">Optional; omit to inherit the environment default.</param>
/// <param name="CompatibilitySurface">Optional; required when a mode is given.</param>
/// <param name="ContentModel">
/// <c>open</c> or <c>closed</c>. Defaults to <c>open</c>, matching JSON Schema's own default.
/// </param>
public sealed record CreateSubjectRequest(
    string Name,
    string Format,
    string Owner,
    string? CompatibilityMode = null,
    string? CompatibilitySurface = null,
    string ContentModel = "open");

/// <summary>Request to register a version.</summary>
/// <param name="Schema">The schema document, as authored.</param>
/// <param name="SemanticVersion">Optional intent label; verified against the verdict.</param>
/// <param name="Changelog">Optional note.</param>
/// <param name="RegisteredBy">Who is registering.</param>
public sealed record RegisterVersionRequest(
    string Schema,
    string? SemanticVersion = null,
    string? Changelog = null,
    string RegisteredBy = "unknown");

/// <summary>Request to check a schema without registering it.</summary>
/// <param name="Schema">The proposed schema document.</param>
public sealed record CheckCompatibilityRequest(string Schema);

/// <summary>Request to decide a pending version.</summary>
/// <param name="DecidedBy">Who is deciding.</param>
public sealed record DecideVersionRequest(string DecidedBy);

/// <summary>Request to change a subject's compatibility policy.</summary>
/// <param name="Mode">Who must keep working, or <see langword="null"/> to inherit.</param>
/// <param name="Surface">How much must keep working.</param>
public sealed record SetCompatibilityPolicyRequest(string? Mode, string? Surface);

/// <summary>Request to look a schema up by its content.</summary>
/// <param name="Format">One of <c>json</c>, <c>avro</c>, <c>protobuf</c>.</param>
/// <param name="Schema">The schema document.</param>
public sealed record LookupSchemaRequest(string Format, string Schema);

// ------------------------------------------------------------------ responses

/// <summary>A compatibility policy on the wire.</summary>
/// <param name="Mode">The who-breaks axis, or <see langword="null"/> when inheriting.</param>
/// <param name="Surface">The what-breaks axis.</param>
public sealed record PolicyResponse(string? Mode, string? Surface)
{
    /// <summary>Projects a policy, or the inheriting state.</summary>
    /// <param name="policy">The policy, or <see langword="null"/>.</param>
    /// <returns>The wire form.</returns>
    public static PolicyResponse From(CompatibilityPolicy? policy) =>
        policy is { } p
            ? new PolicyResponse(WireCase(p.Mode.ToString()), WireCase(p.Surface.ToString()))
            : new PolicyResponse(null, null);

    private static string WireCase(string value) => value.ToUpperInvariant();
}

/// <summary>One registered version.</summary>
/// <param name="Ordinal">The canonical identity within the subject.</param>
/// <param name="SchemaId">The content-addressed schema id.</param>
/// <param name="SemanticVersion">The optional verified label.</param>
/// <param name="Status"><c>ACTIVE</c>, <c>AWAITING_APPROVAL</c> or <c>REJECTED</c>.</param>
/// <param name="Changelog">The note recorded at registration.</param>
/// <param name="RegisteredAt">When it was registered, UTC.</param>
/// <param name="RegisteredBy">Who registered it.</param>
/// <param name="Deprecated">Whether the version is marked deprecated.</param>
public sealed record VersionResponse(
    int Ordinal,
    string SchemaId,
    string? SemanticVersion,
    string Status,
    string? Changelog,
    DateTimeOffset RegisteredAt,
    string RegisteredBy,
    bool Deprecated)
{
    /// <summary>Projects a version.</summary>
    /// <param name="version">The version.</param>
    /// <returns>The wire form.</returns>
    public static VersionResponse From(SchemaVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return new VersionResponse(
            version.Ordinal,
            version.SchemaId.Value,
            version.SemanticVersion?.ToString(),
            StatusToken(version.Status),
            version.Changelog,
            version.RegisteredAt,
            version.RegisteredBy.Value,
            version.Deprecated);
    }

    private static string StatusToken(VersionStatus status) => status switch
    {
        VersionStatus.Active => "ACTIVE",
        VersionStatus.AwaitingApproval => "AWAITING_APPROVAL",
        VersionStatus.Rejected => "REJECTED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

/// <summary>A subject and its versions.</summary>
/// <param name="Name">The subject name.</param>
/// <param name="Format">The schema language.</param>
/// <param name="Owner">Who owns the contract.</param>
/// <param name="Lifecycle"><c>ACTIVE</c>, <c>DEPRECATED</c> or <c>RETIRED</c>.</param>
/// <param name="ContentModel"><c>OPEN</c> or <c>CLOSED</c>.</param>
/// <param name="CompatibilityPolicy">The explicit policy, or nulls when inheriting.</param>
/// <param name="Latest">The gated latest pointer, or <see langword="null"/> when none is active.</param>
/// <param name="Versions">Every version, in ordinal order.</param>
public sealed record SubjectResponse(
    string Name,
    string Format,
    string Owner,
    string Lifecycle,
    string ContentModel,
    PolicyResponse CompatibilityPolicy,
    int? Latest,
    IReadOnlyList<VersionResponse> Versions)
{
    /// <summary>Projects a subject.</summary>
    /// <param name="subject">The subject.</param>
    /// <returns>The wire form.</returns>
    public static SubjectResponse From(Subject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        return new SubjectResponse(
            subject.Name.Value,
            WireTokens.For(subject.Format),
            subject.Owner.Value,
            subject.Lifecycle.ToString().ToUpperInvariant(),
            subject.ContentModel.ToString().ToUpperInvariant(),
            PolicyResponse.From(subject.CompatibilityPolicy),
            subject.Latest?.Ordinal,
            [.. subject.Versions.Select(VersionResponse.From)]);
    }
}

/// <summary>One way a proposal diverges from a prior version.</summary>
/// <param name="Path">A JSON Pointer into the schema document.</param>
/// <param name="Kind">A stable token, for example <c>required_field_added</c>.</param>
/// <param name="Direction"><c>BACKWARD</c> or <c>FORWARD</c>.</param>
/// <param name="Surface"><c>WIRE</c>, <c>WIRE_JSON</c> or <c>SOURCE</c>.</param>
/// <param name="Message">An actionable explanation.</param>
/// <param name="ConflictsWithVersion">The prior version compared against.</param>
public sealed record BreakingChangeResponse(
    string Path,
    string Kind,
    string Direction,
    string Surface,
    string Message,
    int ConflictsWithVersion)
{
    /// <summary>Projects a finding.</summary>
    /// <param name="change">The finding.</param>
    /// <returns>The wire form.</returns>
    public static BreakingChangeResponse From(BreakingChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        return new BreakingChangeResponse(
            change.Path,
            change.Kind,
            change.Direction.ToString().ToUpperInvariant(),
            SurfaceToken(change.Surface),
            change.Message,
            change.ConflictsWithVersion);
    }

    private static string SurfaceToken(CompatibilitySurface surface) => surface switch
    {
        CompatibilitySurface.Wire => "WIRE",
        CompatibilitySurface.WireJson => "WIRE_JSON",
        CompatibilitySurface.Source => "SOURCE",
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, null),
    };
}

/// <summary>The result of a dry-run compatibility check.</summary>
/// <param name="Compatible">Whether the proposal satisfies the policy.</param>
/// <param name="SchemaId">The id the proposal would receive.</param>
/// <param name="Policy">The policy it was evaluated under.</param>
/// <param name="BreakingChanges">Divergences that violate the policy.</param>
/// <param name="AllDivergences">
/// Every divergence, including tolerated ones — this is what explains why a change was allowed.
/// </param>
/// <param name="SuggestedSemver">The label this change warrants.</param>
public sealed record CompatibilityResponse(
    bool Compatible,
    string SchemaId,
    PolicyResponse Policy,
    IReadOnlyList<BreakingChangeResponse> BreakingChanges,
    IReadOnlyList<BreakingChangeResponse> AllDivergences,
    string? SuggestedSemver)
{
    /// <summary>Projects a check result.</summary>
    /// <param name="result">The result.</param>
    /// <returns>The wire form.</returns>
    public static CompatibilityResponse From(CompatibilityCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new CompatibilityResponse(
            result.Compatible,
            result.SchemaId,
            PolicyResponse.From(result.Policy),
            [.. result.Report.BreakingChanges.Select(BreakingChangeResponse.From)],
            [.. result.Report.AllDivergences.Select(BreakingChangeResponse.From)],
            result.SuggestedSemver);
    }
}

/// <summary>A stored schema.</summary>
/// <param name="SchemaId">The content-addressed id.</param>
/// <param name="Format">The schema language.</param>
/// <param name="Schema">The canonical text.</param>
/// <param name="References">Outgoing references, derived from the document.</param>
public sealed record SchemaResponse(
    string SchemaId,
    string Format,
    string Schema,
    IReadOnlyList<ReferenceResponse> References)
{
    /// <summary>Projects a schema.</summary>
    /// <param name="schema">The schema.</param>
    /// <returns>The wire form.</returns>
    public static SchemaResponse From(Schema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        return new SchemaResponse(
            schema.Id.Value,
            WireTokens.For(schema.Format),
            schema.Body,
            [.. schema.References.Select(r => new ReferenceResponse(r.Name, r.Subject.Value, r.Version))]);
    }
}

/// <summary>One outgoing reference.</summary>
/// <param name="Name">The <c>$ref</c> text as it appears in the document.</param>
/// <param name="Subject">The referenced subject.</param>
/// <param name="Version">The referenced version ordinal.</param>
public sealed record ReferenceResponse(string Name, string Subject, int Version);

/// <summary>The outcome of registering a version.</summary>
/// <param name="Subject">The subject name.</param>
/// <param name="Ordinal">The allocated ordinal.</param>
/// <param name="SchemaId">The content-addressed id.</param>
/// <param name="Status">Whether it is active or awaiting approval.</param>
/// <param name="Created">
/// <see langword="false"/> when the schema was already at the tip and no ordinal was allocated.
/// </param>
/// <param name="Divergences">Findings, including ones the policy tolerated.</param>
public sealed record RegisterVersionResponse(
    string Subject,
    int Ordinal,
    string SchemaId,
    string Status,
    bool Created,
    IReadOnlyList<BreakingChangeResponse> Divergences);
