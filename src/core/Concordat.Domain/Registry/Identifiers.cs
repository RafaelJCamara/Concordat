using Concordat.Domain.Results;

namespace Concordat.Domain.Registry;

/// <summary>
/// The surrogate identity of a <see cref="Subject"/>.
/// </summary>
/// <param name="Value">The underlying identifier.</param>
/// <remarks>
/// A surrogate rather than using <see cref="SubjectName"/> as the key: later milestones need
/// to reference a subject from approval, audit and service-registration rows, and a mutable
/// natural key propagated into every foreign key would be painful to change.
/// </remarks>
public readonly record struct SubjectId(Guid Value)
{
    /// <summary>Creates a new identifier.</summary>
    /// <returns>A fresh <see cref="SubjectId"/>.</returns>
    /// <remarks>Version 7 for index locality: these become clustered keys in M1.5.</remarks>
    public static SubjectId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>
/// The identity of an environment (ADR-012).
/// </summary>
/// <param name="Value">The underlying identifier.</param>
/// <remarks>
/// Environments are a first-class aggregate in M7, but subjects are scoped by environment
/// from the start. Carrying the identifier now avoids adding a required foreign key and
/// rebuilding a unique index on a populated table later — the same reasoning that puts
/// <c>ITenantContext</c> into M1.5 with a single implicit tenant.
/// </remarks>
public readonly record struct EnvironmentId(Guid Value)
{
    /// <summary>Creates a new identifier.</summary>
    /// <returns>A fresh <see cref="EnvironmentId"/>.</returns>
    public static EnvironmentId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>
/// Who performed an action: a user, a service account, or an API key label.
/// </summary>
/// <remarks>
/// An opaque string in M1.1. Identity is M8; until then the domain only needs something
/// non-empty to attribute registrations and approvals to in the audit trail.
/// </remarks>
public sealed record ActorId
{
    /// <summary>The longest permitted actor identifier, in characters.</summary>
    public const int MaxLength = 256;

    private ActorId(string value) => Value = value;

    /// <summary>The identifier.</summary>
    public string Value { get; }

    /// <summary>Parses and validates an actor identifier.</summary>
    /// <param name="value">Candidate text. Surrounding whitespace is trimmed.</param>
    /// <returns>
    /// The identifier, or a failure carrying <see cref="ConcordatCodes.ActorIdInvalid"/>.
    /// </returns>
    public static Result<ActorId> Create(string? value)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result<ActorId>.Failure(
                ConcordatCodes.ActorIdInvalid, "An actor id is required.");
        }

        return trimmed.Length > MaxLength
            ? Result<ActorId>.Failure(
                ConcordatCodes.ActorIdInvalid,
                $"An actor id may be at most {MaxLength} characters; got {trimmed.Length}.")
            : Result<ActorId>.Success(new ActorId(trimmed));
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
