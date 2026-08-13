namespace Concordat.Domain.Registry;

/// <summary>
/// Where a version sits in the approval gate (ADR-017).
/// </summary>
public enum VersionStatus
{
    /// <summary>Registered and eligible to be the <c>latest</c> pointer's target.</summary>
    Active = 1,

    /// <summary>
    /// Registered as a reviewable artifact because it breaks the subject's policy. It does not
    /// move the <c>latest</c> pointer and no consumer tracking <c>latest</c> sees it.
    /// </summary>
    AwaitingApproval = 2,

    /// <summary>
    /// A proposal that was reviewed and declined. Kept rather than deleted so the history of
    /// what was attempted survives.
    /// </summary>
    Rejected = 3,
}

/// <summary>
/// The lifecycle of a subject.
/// </summary>
public enum SubjectLifecycle
{
    /// <summary>In use, accepting new versions.</summary>
    Active = 1,

    /// <summary>
    /// Advisory only: signals that consumers should migrate away, but still accepts versions
    /// so existing producers can patch their contract.
    /// </summary>
    Deprecated = 2,

    /// <summary>The soft delete. Accepts no further versions and no lifecycle transitions.</summary>
    Retired = 3,
}

/// <summary>
/// The explicit, gated <c>latest</c> pointer (ADR-017).
/// </summary>
/// <param name="Ordinal">The version the pointer targets.</param>
/// <param name="MovedAt">When the pointer last moved.</param>
/// <param name="MovedBy">Who moved it.</param>
/// <remarks>
/// Deliberately not "whichever ordinal is highest". Confluent's <c>latest</c> is a mutable
/// shared pointer, so a third party registering a version silently changes what your producer
/// serialises with, at runtime, with no deploy. Making the move an explicit recorded act
/// removes that.
/// </remarks>
public sealed record LatestPointer(int Ordinal, DateTimeOffset MovedAt, ActorId MovedBy);

/// <summary>
/// One registered version of a subject.
/// </summary>
/// <remarks>
/// An entity inside the <see cref="Subject"/> aggregate: it has identity (the ordinal) and
/// mutable status, but it is only ever reached and changed through its subject, which is what
/// keeps the ordinal and pointer invariants enforceable.
/// </remarks>
public sealed class SchemaVersion
{
    internal SchemaVersion(
        int ordinal,
        SchemaId schemaId,
        SemanticVersion? semanticVersion,
        string? changelog,
        VersionStatus status,
        DateTimeOffset registeredAt,
        ActorId registeredBy)
    {
        Ordinal = ordinal;
        SchemaId = schemaId;
        SemanticVersion = semanticVersion;
        Changelog = changelog;
        Status = status;
        RegisteredAt = registeredAt;
        RegisteredBy = registeredBy;
    }

    /// <summary>The canonical, immutable, totally ordered identity within the subject (ADR-004).</summary>
    public int Ordinal { get; }

    /// <summary>The content-addressed schema this version points at.</summary>
    public SchemaId SchemaId { get; }

    /// <summary>The optional, verified intent label (ADR-004).</summary>
    public SemanticVersion? SemanticVersion { get; }

    /// <summary>A human note about what changed.</summary>
    public string? Changelog { get; }

    /// <summary>Where this version sits in the approval gate.</summary>
    public VersionStatus Status { get; private set; }

    /// <summary>Whether this version is marked deprecated.</summary>
    public bool Deprecated { get; private set; }

    /// <summary>When the version was registered.</summary>
    public DateTimeOffset RegisteredAt { get; }

    /// <summary>Who registered it.</summary>
    public ActorId RegisteredBy { get; }

    /// <summary>When the version was approved or rejected, if it ever was.</summary>
    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>Who approved or rejected it.</summary>
    public ActorId? DecidedBy { get; private set; }

    internal void Approve(DateTimeOffset at, ActorId by)
    {
        Status = VersionStatus.Active;
        DecidedAt = at;
        DecidedBy = by;
    }

    internal void Reject(DateTimeOffset at, ActorId by)
    {
        Status = VersionStatus.Rejected;
        DecidedAt = at;
        DecidedBy = by;
    }

    internal void MarkDeprecated() => Deprecated = true;
}
