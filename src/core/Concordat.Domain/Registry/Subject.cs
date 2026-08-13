using Concordat.Domain.Results;

namespace Concordat.Domain.Registry;

/// <summary>
/// The result of registering a version.
/// </summary>
/// <param name="Version">The version, whether newly created or already present.</param>
/// <param name="Created">
/// <see langword="false"/> when the schema was already at the tip and no ordinal was
/// allocated. Registration is idempotent at the tip so a retried publish does not inflate the
/// version history.
/// </param>
public sealed record RegistrationOutcome(SchemaVersion Version, bool Created);

/// <summary>
/// A versioned contract for one message type, and the aggregate root that owns every
/// invariant over its versions.
/// </summary>
/// <remarks>
/// <para>
/// The aggregate enforces: one format across all versions; contiguous, monotonic ordinals
/// from 1; the ADR-017 approval gate; the ADR-004 semver check; and soft delete via
/// <see cref="SubjectLifecycle.Retired"/>.
/// </para>
/// <para>
/// It does <em>not</em> evaluate compatibility. A <see cref="CompatibilityVerdict"/> is an
/// input, computed by M1.3 and passed in.
/// </para>
/// </remarks>
public sealed class Subject
{
    /// <summary>The longest permitted changelog, in characters.</summary>
    public const int MaxChangelogLength = 4000;

    private readonly List<SchemaVersion> _versions;

    private Subject(
        SubjectId id,
        EnvironmentId environmentId,
        SubjectName name,
        SchemaFormat format,
        ActorId owner,
        CompatibilityPolicy? compatibilityPolicy,
        ContentModel contentModel,
        DateTimeOffset createdAt,
        List<SchemaVersion> versions,
        LatestPointer? latest,
        SubjectLifecycle lifecycle)
    {
        Id = id;
        EnvironmentId = environmentId;
        Name = name;
        Format = format;
        Owner = owner;
        CompatibilityPolicy = compatibilityPolicy;
        ContentModel = contentModel;
        CreatedAt = createdAt;
        _versions = versions;
        Latest = latest;
        Lifecycle = lifecycle;
    }

    // Materialisation only. EF cannot bind an owned collection through a constructor, so it
    // needs a parameterless one and populates the backing fields directly. The nulls are
    // immediately overwritten; nothing else may call this.
    private Subject()
    {
        _versions = [];
        Name = null!;
        Owner = null!;
    }

    /// <summary>The surrogate identity.</summary>
    public SubjectId Id { get; }

    /// <summary>The environment this subject belongs to (ADR-012).</summary>
    public EnvironmentId EnvironmentId { get; }

    /// <summary>The subject name (ADR-011).</summary>
    public SubjectName Name { get; }

    /// <summary>The schema language, fixed for the subject's whole life.</summary>
    public SchemaFormat Format { get; }

    /// <summary>Who owns this contract.</summary>
    public ActorId Owner { get; private set; }

    /// <summary>
    /// The compatibility policy, or <see langword="null"/> to inherit the environment default.
    /// </summary>
    /// <remarks>
    /// Nullable on purpose. Copying the environment default at creation would permanently
    /// destroy the distinction between "explicitly configured" and "following the default",
    /// and there would be no data from which to reconstruct it later.
    /// </remarks>
    public CompatibilityPolicy? CompatibilityPolicy { get; private set; }

    /// <summary>
    /// Whether documents may carry properties the schema does not describe.
    /// </summary>
    /// <remarks>
    /// Non-nullable and explicit, unlike <see cref="CompatibilityPolicy"/>. Inferring it from
    /// each schema document would let it flip silently between versions and change the meaning
    /// of every subsequent verdict (DESIGN §7).
    /// </remarks>
    public ContentModel ContentModel { get; private set; }

    /// <summary>The lifecycle state.</summary>
    public SubjectLifecycle Lifecycle { get; private set; }

    /// <summary>When the subject was created.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Every version, in ordinal order.</summary>
    /// <remarks>
    /// Wrapped rather than returned directly: an <see cref="IReadOnlyList{T}"/> backed by a
    /// <see cref="List{T}"/> can be cast back to <see cref="ICollection{T}"/> and mutated,
    /// which would bypass every ordinal invariant on this aggregate.
    /// </remarks>
    public IReadOnlyList<SchemaVersion> Versions => _versions.AsReadOnly();

    /// <summary>
    /// The gated <c>latest</c> pointer, or <see langword="null"/> when no version is active yet.
    /// </summary>
    public LatestPointer? Latest { get; private set; }

    /// <summary>Creates a subject with no versions.</summary>
    /// <param name="environmentId">The owning environment.</param>
    /// <param name="name">The subject name.</param>
    /// <param name="format">The schema language, fixed for the subject's whole life.</param>
    /// <param name="owner">Who owns the contract.</param>
    /// <param name="compatibilityPolicy">
    /// An explicit policy, or <see langword="null"/> to inherit the environment default.
    /// </param>
    /// <param name="createdAt">When the subject was created. Normalised to UTC.</param>
    /// <param name="contentModel">
    /// Whether unknown properties are permitted. Defaults to <see cref="ContentModel.Open"/>,
    /// matching JSON Schema's own default.
    /// </param>
    /// <returns>The subject.</returns>
    public static Result<Subject> Create(
        EnvironmentId environmentId,
        SubjectName name,
        SchemaFormat format,
        ActorId owner,
        CompatibilityPolicy? compatibilityPolicy,
        DateTimeOffset createdAt,
        ContentModel contentModel = ContentModel.Open) =>
        Result<Subject>.Success(new Subject(
            SubjectId.New(),
            environmentId,
            name,
            format,
            owner,
            compatibilityPolicy,
            contentModel,
            createdAt.ToUniversalTime(),
            [],
            latest: null,
            SubjectLifecycle.Active));

    /// <summary>
    /// Registers a new version, applying the approval gate and the semver check.
    /// </summary>
    /// <param name="schema">The schema being registered.</param>
    /// <param name="verdict">
    /// The compatibility engine's answer, evaluated against <see cref="EffectivePolicy"/>.
    /// Ignored when this is the first version, which cannot break anything.
    /// </param>
    /// <param name="semanticVersion">The optional intent label, verified against the verdict.</param>
    /// <param name="changelog">An optional human note.</param>
    /// <param name="registeredBy">Who is registering.</param>
    /// <param name="registeredAt">When. Normalised to UTC.</param>
    /// <returns>
    /// The registered version, or the first failing rule. A breaking change succeeds with
    /// <see cref="VersionStatus.AwaitingApproval"/> and leaves <see cref="Latest"/> untouched.
    /// </returns>
    public Result<RegistrationOutcome> RegisterVersion(
        Schema schema,
        CompatibilityVerdict verdict,
        SemanticVersion? semanticVersion,
        string? changelog,
        ActorId registeredBy,
        DateTimeOffset registeredAt)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(verdict);

        if (Lifecycle is SubjectLifecycle.Retired)
        {
            return Result<RegistrationOutcome>.Failure(
                ConcordatCodes.SubjectRetired,
                $"Subject '{Name}' is retired and accepts no further versions.");
        }

        if (schema.Format != Format)
        {
            return Result<RegistrationOutcome>.Failure(
                ConcordatCodes.FormatMismatch,
                $"Subject '{Name}' is {Format}; the schema is {schema.Format}. " +
                "A subject keeps one format for its whole life.");
        }

        var changelogCheck = ValidateChangelog(changelog, out var normalisedChangelog);
        if (changelogCheck.IsFailure)
        {
            return Result<RegistrationOutcome>.Failure(changelogCheck.Error!);
        }

        var isFirstVersion = _versions.Count == 0;

        if (isFirstVersion && verdict.IsBreaking)
        {
            return Result<RegistrationOutcome>.Failure(
                ConcordatCodes.FirstVersionCannotBreak,
                $"The first version of '{Name}' has no predecessor and cannot be breaking.");
        }

        // The aggregate cannot recompute the verdict, but it can reject one computed against a
        // different policy than the subject's own.
        if (CompatibilityPolicy is { } explicitPolicy &&
            !isFirstVersion &&
            verdict.EvaluatedUnder != explicitPolicy)
        {
            return Result<RegistrationOutcome>.Failure(
                ConcordatCodes.VerdictPolicyMismatch,
                $"The verdict was evaluated under {verdict.EvaluatedUnder}, but subject " +
                $"'{Name}' requires {explicitPolicy}.");
        }

        var isBreaking = !isFirstVersion && verdict.IsBreaking;

        var tip = ActiveTip();
        if (tip is not null && tip.SchemaId == schema.Id)
        {
            // Idempotent at the tip: a retried publish of the current schema must not allocate
            // an ordinal. Re-registering an *older* schema is a genuine revert and does.
            return Result<RegistrationOutcome>.Success(new RegistrationOutcome(tip, Created: false));
        }

        var semverCheck = VerifySemanticVersion(semanticVersion, isBreaking);
        if (semverCheck.IsFailure)
        {
            return Result<RegistrationOutcome>.Failure(semverCheck.Error!);
        }

        var version = new SchemaVersion(
            NextOrdinal(),
            schema.Id,
            semanticVersion,
            normalisedChangelog,
            isBreaking ? VersionStatus.AwaitingApproval : VersionStatus.Active,
            registeredAt.ToUniversalTime(),
            registeredBy);

        _versions.Add(version);

        if (!isBreaking)
        {
            MoveLatest(version.Ordinal, registeredAt.ToUniversalTime(), registeredBy);
        }

        return Result<RegistrationOutcome>.Success(new RegistrationOutcome(version, Created: true));
    }

    /// <summary>Approves a pending breaking version, making it active (ADR-017).</summary>
    /// <param name="ordinal">The version to approve.</param>
    /// <param name="approvedBy">Who is approving.</param>
    /// <param name="approvedAt">When. Normalised to UTC.</param>
    /// <returns>Success, or the first failing rule.</returns>
    /// <remarks>
    /// Approval advances <see cref="Latest"/> only when the approved version is ahead of the
    /// current target. If a compatible later version registered while this one was pending,
    /// approving it must not drag the pointer backwards.
    /// </remarks>
    public Result Approve(int ordinal, ActorId approvedBy, DateTimeOffset approvedAt)
    {
        var pending = FindPending(ordinal);
        if (pending.IsFailure)
        {
            return Result.Failure(pending.Error!);
        }

        var at = approvedAt.ToUniversalTime();
        pending.Value.Approve(at, approvedBy);

        if (Latest is null || ordinal > Latest.Ordinal)
        {
            MoveLatest(ordinal, at, approvedBy);
        }

        return Result.Success();
    }

    /// <summary>Rejects a pending breaking version. The pointer does not move (ADR-017).</summary>
    /// <param name="ordinal">The version to reject.</param>
    /// <param name="rejectedBy">Who is rejecting.</param>
    /// <param name="rejectedAt">When. Normalised to UTC.</param>
    /// <returns>Success, or the first failing rule.</returns>
    public Result Reject(int ordinal, ActorId rejectedBy, DateTimeOffset rejectedAt)
    {
        var pending = FindPending(ordinal);
        if (pending.IsFailure)
        {
            return Result.Failure(pending.Error!);
        }

        pending.Value.Reject(rejectedAt.ToUniversalTime(), rejectedBy);
        return Result.Success();
    }

    /// <summary>Transfers ownership.</summary>
    /// <param name="owner">The new owner.</param>
    /// <returns>Success, or a failure when the subject is retired.</returns>
    public Result ChangeOwner(ActorId owner)
    {
        if (Lifecycle is SubjectLifecycle.Retired)
        {
            return Result.Failure(
                ConcordatCodes.LifecycleTransitionInvalid,
                $"Subject '{Name}' is retired.");
        }

        Owner = owner;
        return Result.Success();
    }

    /// <summary>Marks the subject deprecated. Advisory: it still accepts versions.</summary>
    /// <returns>Success, or a failure when the subject is retired.</returns>
    public Result Deprecate()
    {
        if (Lifecycle is SubjectLifecycle.Retired)
        {
            return Result.Failure(
                ConcordatCodes.LifecycleTransitionInvalid,
                $"Subject '{Name}' is retired and cannot be deprecated.");
        }

        Lifecycle = SubjectLifecycle.Deprecated;
        return Result.Success();
    }

    /// <summary>Retires the subject. This is the soft delete and is terminal.</summary>
    /// <returns>Success, or a failure when the subject is already retired.</returns>
    public Result Retire()
    {
        if (Lifecycle is SubjectLifecycle.Retired)
        {
            return Result.Failure(
                ConcordatCodes.LifecycleTransitionInvalid,
                $"Subject '{Name}' is already retired.");
        }

        Lifecycle = SubjectLifecycle.Retired;
        return Result.Success();
    }

    /// <summary>Replaces the compatibility policy, or clears it to inherit the environment default.</summary>
    /// <param name="policy">The new policy, or <see langword="null"/> to inherit.</param>
    /// <returns>Success, or a failure when the subject is retired.</returns>
    public Result SetCompatibilityPolicy(CompatibilityPolicy? policy)
    {
        if (Lifecycle is SubjectLifecycle.Retired)
        {
            return Result.Failure(
                ConcordatCodes.LifecycleTransitionInvalid,
                $"Subject '{Name}' is retired.");
        }

        CompatibilityPolicy = policy;
        return Result.Success();
    }

    /// <summary>Changes the content model.</summary>
    /// <param name="contentModel">Whether unknown properties are permitted.</param>
    /// <returns>Success, or a failure when the subject is retired.</returns>
    /// <remarks>
    /// Changing this alters how every future compatibility check is evaluated, but it does not
    /// retroactively invalidate versions already registered.
    /// </remarks>
    public Result SetContentModel(ContentModel contentModel)
    {
        if (Lifecycle is SubjectLifecycle.Retired)
        {
            return Result.Failure(
                ConcordatCodes.LifecycleTransitionInvalid,
                $"Subject '{Name}' is retired.");
        }

        ContentModel = contentModel;
        return Result.Success();
    }

    /// <summary>Resolves the policy in force, given the environment default.</summary>
    /// <param name="environmentDefault">The environment's default policy.</param>
    /// <returns>The subject's explicit policy when set, otherwise the environment default.</returns>
    public CompatibilityPolicy EffectivePolicy(CompatibilityPolicy environmentDefault) =>
        CompatibilityPolicy ?? environmentDefault;

    /// <summary>The version the <c>latest</c> pointer targets, if any.</summary>
    /// <returns>The targeted version, or <see langword="null"/>.</returns>
    public SchemaVersion? LatestVersion() =>
        Latest is null ? null : _versions.Find(v => v.Ordinal == Latest.Ordinal);

    private SchemaVersion? ActiveTip() =>
        Latest is null ? null : _versions.Find(v => v.Ordinal == Latest.Ordinal);

    private int NextOrdinal() => _versions.Count == 0 ? 1 : _versions[^1].Ordinal + 1;

    private void MoveLatest(int ordinal, DateTimeOffset at, ActorId by) =>
        Latest = new LatestPointer(ordinal, at, by);

    private Result<SchemaVersion> FindPending(int ordinal)
    {
        var version = _versions.Find(v => v.Ordinal == ordinal);

        if (version is null)
        {
            return Result<SchemaVersion>.Failure(
                ConcordatCodes.VersionNotFound,
                $"Subject '{Name}' has no version {ordinal}.");
        }

        return version.Status is not VersionStatus.AwaitingApproval
            ? Result<SchemaVersion>.Failure(
                ConcordatCodes.VersionNotAwaitingApproval,
                $"Version {ordinal} of '{Name}' is {version.Status}, not awaiting approval.")
            : Result<SchemaVersion>.Success(version);
    }

    // Returns Result rather than Result<string?> because a null changelog is a legitimate
    // success, and Result<T> is constrained to notnull so that Value is never a silent null.
    private static Result ValidateChangelog(string? changelog, out string? normalised)
    {
        var trimmed = changelog?.Trim();
        normalised = string.IsNullOrEmpty(trimmed) ? null : trimmed;

        return normalised is { Length: > MaxChangelogLength }
            ? Result.Failure(
                ConcordatCodes.ChangelogTooLong,
                $"A changelog may be at most {MaxChangelogLength} characters; got {normalised.Length}.")
            : Result.Success();
    }

    private Result VerifySemanticVersion(SemanticVersion? proposed, bool isBreaking)
    {
        if (proposed is not { } label)
        {
            return Result.Success();
        }

        var previous = _versions
            .Where(v => v.SemanticVersion is not null && v.Status is not VersionStatus.Rejected)
            .Select(v => v.SemanticVersion!.Value)
            .DefaultIfEmpty()
            .Max();

        if (previous != default && label <= previous)
        {
            return Result.Failure(
                ConcordatCodes.SemverNotIncreasing,
                $"Label {label} does not increase on {previous}.");
        }

        // ADR-004: over-claiming is allowed, under-claiming is not. A breaking change labelled
        // MINOR or PATCH is the lie the registry exists to prevent.
        if (isBreaking && previous != default && !label.IsMajorBumpOver(previous))
        {
            return Result.Failure(
                ConcordatCodes.SemverLabelUnderstatesBreakage,
                $"This change is breaking, so {label} must increase the major component of " +
                $"{previous}.");
        }

        if (isBreaking && previous == default && label.Major == 0)
        {
            return Result.Failure(
                ConcordatCodes.SemverLabelUnderstatesBreakage,
                $"This change is breaking, so {label} must carry a non-zero major component.");
        }

        return Result.Success();
    }
}
