using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Application.Registry;

/// <summary>How many subjects an environment has, by lifecycle.</summary>
/// <param name="Active">In use, accepting versions.</param>
/// <param name="Deprecated">Advisory: consumers are asked to migrate away.</param>
/// <param name="Retired">Soft-deleted. Still counted: the subject list still lists them.</param>
public sealed record SubjectTotals(int Active, int Deprecated, int Retired)
{
    /// <summary>Every subject, whatever its lifecycle.</summary>
    public int Total => Active + Deprecated + Retired;
}

/// <summary>How many versions an environment holds, by where each sits in the approval gate.</summary>
/// <param name="Active">Registered and eligible to be what <c>latest</c> points at.</param>
/// <param name="AwaitingApproval">Held at the gate (ADR-017).</param>
/// <param name="Rejected">Proposals a reviewer declined.</param>
/// <param name="Dismissed">Proposals reverted before anyone reviewed them.</param>
public sealed record VersionTotals(int Active, int AwaitingApproval, int Rejected, int Dismissed)
{
    /// <summary>Every version, whatever its status.</summary>
    public int Total => Active + AwaitingApproval + Rejected + Dismissed;
}

/// <summary>One of the most recently created subjects, with just what a card needs.</summary>
/// <param name="Name">The subject name.</param>
/// <param name="Format">The schema language.</param>
/// <param name="Owner">Who owns the contract.</param>
/// <param name="Lifecycle">Where the subject is in its life.</param>
/// <param name="Latest">What the gated <c>latest</c> pointer targets, or null when none is active.</param>
/// <param name="Versions">How many versions it has, of any status.</param>
/// <param name="AwaitingApproval">How many of them are held at the gate.</param>
/// <param name="CreatedAt">When the subject was created.</param>
public sealed record NewestSubject(
    string Name,
    SchemaFormat Format,
    string Owner,
    SubjectLifecycle Lifecycle,
    int? Latest,
    int Versions,
    int AwaitingApproval,
    DateTimeOffset CreatedAt);

/// <summary>A version the approval gate is holding, or turned away.</summary>
/// <param name="Subject">The subject it belongs to.</param>
/// <param name="Ordinal">Its ordinal within the subject.</param>
/// <param name="Status">Awaiting approval or rejected, and never anything else.</param>
/// <param name="SemanticVersion">The verified intent label, when it carried one.</param>
/// <param name="RegisteredAt">When it was proposed.</param>
/// <param name="RegisteredBy">Who proposed it.</param>
/// <param name="DecidedAt">When it was rejected; null while it is still awaiting a decision.</param>
/// <param name="DecidedBy">Who rejected it; null while it is still awaiting a decision.</param>
public sealed record HeldVersion(
    string Subject,
    int Ordinal,
    VersionStatus Status,
    string? SemanticVersion,
    DateTimeOffset RegisteredAt,
    string RegisteredBy,
    DateTimeOffset? DecidedAt,
    string? DecidedBy);

/// <summary>What is declared under one enforcement mode.</summary>
/// <param name="Mode">The mode.</param>
/// <param name="Contracts">How many contracts are set to it.</param>
/// <param name="PublishBindings">How many publish bindings those contracts carry.</param>
/// <param name="ConsumeBindings">How many consume bindings those contracts carry.</param>
public sealed record EnforcementTier(
    EnforcementMode Mode, int Contracts, int PublishBindings, int ConsumeBindings);

/// <summary>How many distinct violations SDKs have reported in an environment.</summary>
/// <param name="Distinct">Every distinct violation ever reported, which is what is stored.</param>
/// <param name="Recent">How many of them were last seen inside the window.</param>
public sealed record ViolationTotals(int Distinct, int Recent);

/// <summary>
/// The overview of one environment: what the dashboard shows, computed where the data is.
/// </summary>
/// <param name="Subjects">Subjects by lifecycle.</param>
/// <param name="Versions">Versions by approval status.</param>
/// <param name="NewestSubjects">The most recently created subjects, newest first.</param>
/// <param name="BreakingChanges">
/// The most recently proposed versions held at the gate or rejected, newest first.
/// </param>
/// <param name="Enforcement">
/// Always one entry per mode, in the order Off, Monitor, Enforce, including modes nothing is set to.
/// </param>
/// <param name="Violations">Violations SDKs have reported.</param>
public sealed record EnvironmentSummary(
    SubjectTotals Subjects,
    VersionTotals Versions,
    IReadOnlyList<NewestSubject> NewestSubjects,
    IReadOnlyList<HeldVersion> BreakingChanges,
    IReadOnlyList<EnforcementTier> Enforcement,
    ViolationTotals Violations);

/// <summary>Reads the overview of one environment (ADR-037).</summary>
/// <param name="EnvironmentId">The environment.</param>
public sealed record GetEnvironmentSummaryQuery(EnvironmentId EnvironmentId)
    : IQuery<EnvironmentSummary>
{
    /// <summary>How many newest subjects the overview carries.</summary>
    /// <remarks>
    /// Fixed rather than a parameter: the bound is what keeps the cost of this route independent
    /// of how many subjects there are, and a caller-chosen limit is a caller-chosen cost.
    /// </remarks>
    public const int NewestSubjectsLimit = 6;

    /// <summary>How many held or rejected versions the overview carries.</summary>
    public const int BreakingChangesLimit = 5;

    /// <summary>How far back a violation must have been seen to count as recent.</summary>
    public static readonly TimeSpan ViolationWindow = TimeSpan.FromHours(24);
}

/// <summary>Handles <see cref="GetEnvironmentSummaryQuery"/>.</summary>
public sealed class GetEnvironmentSummaryHandler(
    IEnvironmentSummaryReader reader, TimeProvider clock)
    : IQueryHandler<GetEnvironmentSummaryQuery, EnvironmentSummary>
{
    /// <inheritdoc />
    public async Task<Result<EnvironmentSummary>> HandleAsync(
        GetEnvironmentSummaryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // No existence check, on purpose, and the same answer the subject list gives: an
        // environment nothing has been written to is an empty one, not a missing one. Refusing it
        // would turn a fresh install's landing page, before the first subject exists, into an
        // error screen at the one moment it should be offering to create that subject.
        var summary = await reader.ReadAsync(
            query.EnvironmentId,
            GetEnvironmentSummaryQuery.NewestSubjectsLimit,
            GetEnvironmentSummaryQuery.BreakingChangesLimit,
            clock.GetUtcNow() - GetEnvironmentSummaryQuery.ViolationWindow,
            cancellationToken).ConfigureAwait(false);

        return Result<EnvironmentSummary>.Success(summary);
    }
}
