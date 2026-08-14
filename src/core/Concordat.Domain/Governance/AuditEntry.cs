using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Domain.Governance;

/// <summary>
/// What happened. One member per state change the registry can make (M7.4).
/// </summary>
/// <remarks>
/// Health checks are deliberately absent. A probe that runs on a timer would produce most of
/// the rows in the table and none of the answers anyone opens an audit log for.
/// </remarks>
public enum AuditAction
{
    /// <summary>A subject was created.</summary>
    SubjectCreated,

    /// <summary>A subject's policy, description or lifecycle changed.</summary>
    SubjectUpdated,

    /// <summary>A subject was retired.</summary>
    SubjectRetired,

    /// <summary>A version was registered and became active immediately.</summary>
    VersionRegistered,

    /// <summary>A version was registered and is waiting for a reviewer (ADR-017).</summary>
    VersionSubmitted,

    /// <summary>A reviewer approved a pending version.</summary>
    VersionApproved,

    /// <summary>A reviewer rejected a pending version.</summary>
    VersionRejected,

    /// <summary>A pending version was dismissed because the change was reverted.</summary>
    VersionDismissed,

    /// <summary>A version was promoted into another environment.</summary>
    VersionPromoted,

    /// <summary>An environment was created.</summary>
    EnvironmentCreated,

    /// <summary>An environment's settings changed.</summary>
    EnvironmentUpdated,

    /// <summary>A broker was added to an environment.</summary>
    BrokerAdded,

    /// <summary>A broker was removed from an environment.</summary>
    BrokerRemoved,

    /// <summary>A broker's credentials were set or rotated.</summary>
    BrokerCredentialSet,

    /// <summary>A broker's credentials were removed.</summary>
    BrokerCredentialRemoved,

    /// <summary>A contract was created.</summary>
    ContractCreated,

    /// <summary>A binding was added to a contract.</summary>
    ContractBindingAdded,

    /// <summary>A contract's enforcement mode changed.</summary>
    ContractEnforcementChanged,

    /// <summary>A service declared its producer/consumer intent.</summary>
    ServiceRegistered,

    /// <summary>An account was added to the tenant (M8).</summary>
    MemberAdded,

    /// <summary>A member's role changed.</summary>
    MemberRoleChanged,

    /// <summary>An API key was issued.</summary>
    ApiKeyIssued,

    /// <summary>An API key was revoked.</summary>
    ApiKeyRevoked,
}

/// <summary>
/// One line in the audit trail: who did what to which thing, and when.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only, and written in the same transaction as the change it records.</b> An audit
/// row saved separately can outlive a rolled-back change or be lost while the change survives,
/// and an audit log that can disagree with the data is worth less than no audit log — it is
/// wrong in a way that reads as authoritative.
/// </para>
/// <para>
/// <b>It records what happened, not what was attempted.</b> A refused request never reaches
/// <c>SaveChanges</c>, so there is no transaction to ride along with. A breaking registration
/// is not a refusal — it succeeds and lands as <see cref="AuditAction.VersionSubmitted"/>,
/// which is the case governance actually cares about.
/// </para>
/// </remarks>
public sealed class AuditEntry
{
    /// <summary>The longest permitted target, in characters.</summary>
    public const int MaxTargetLength = 512;

    /// <summary>The longest permitted detail, in characters.</summary>
    public const int MaxDetailLength = 2048;

    private AuditEntry(
        Guid id,
        EnvironmentId? environmentId,
        AuditAction action,
        ActorId actor,
        string target,
        string? detail,
        DateTimeOffset at)
    {
        Id = id;
        EnvironmentId = environmentId;
        Action = action;
        Actor = actor;
        Target = target;
        Detail = detail;
        At = at;
    }

    /// <summary>The entry's identity.</summary>
    public Guid Id { get; }

    /// <summary>Which environment, or null for a change that is not scoped to one.</summary>
    public EnvironmentId? EnvironmentId { get; }

    /// <summary>What happened.</summary>
    public AuditAction Action { get; }

    /// <summary>Who did it.</summary>
    public ActorId Actor { get; private set; } = null!;

    /// <summary>What it happened to — a subject name, contract name or broker address.</summary>
    public string Target { get; private set; } = null!;

    /// <summary>One line of context, or null.</summary>
    public string? Detail { get; private set; }

    /// <summary>When, in UTC.</summary>
    public DateTimeOffset At { get; }

    /// <summary>Records an entry.</summary>
    /// <param name="environmentId">Which environment, or null.</param>
    /// <param name="action">What happened.</param>
    /// <param name="actor">Who did it.</param>
    /// <param name="target">What it happened to.</param>
    /// <param name="at">When.</param>
    /// <param name="detail">One line of context.</param>
    /// <returns>The entry.</returns>
    /// <remarks>
    /// Over-long targets and details are truncated rather than refused. Auditing is a side
    /// effect of a change the caller already asked for and the domain already allowed; failing
    /// the change because its description does not fit would make the log a source of outages.
    /// </remarks>
    public static AuditEntry Record(
        EnvironmentId? environmentId,
        AuditAction action,
        ActorId actor,
        string? target,
        DateTimeOffset at,
        string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return new AuditEntry(
            Guid.CreateVersion7(),
            environmentId,
            action,
            actor,
            Clamp(target ?? string.Empty, MaxTargetLength),
            detail is null ? null : Clamp(detail, MaxDetailLength),
            at.ToUniversalTime());
    }

    private static string Clamp(string value, int max) =>
        value.Length <= max ? value : value[..max];

    /// <summary>Rehydrates an entry from storage.</summary>
    /// <param name="id">The identity.</param>
    /// <param name="environmentId">Which environment.</param>
    /// <param name="action">What happened.</param>
    /// <param name="actor">Who did it.</param>
    /// <param name="target">What it happened to.</param>
    /// <param name="detail">The context.</param>
    /// <param name="at">When.</param>
    /// <returns>The entry.</returns>
    /// <remarks>Persistence only; it performs no validation because storage is trusted.</remarks>
    public static AuditEntry FromStorage(
        Guid id,
        EnvironmentId? environmentId,
        AuditAction action,
        ActorId actor,
        string target,
        string? detail,
        DateTimeOffset at) =>
        new(id, environmentId, action, actor, target, detail, at);
}

/// <summary>The wire spellings of <see cref="AuditAction"/>.</summary>
/// <remarks>
/// Separate from <see cref="WireTokens"/> only because that type lives in the registry
/// context; the rule is the same one, for the same reason — a C# rename must not change the
/// wire format, and these strings are what an operator's saved audit filter is written
/// against.
/// </remarks>
public static class AuditTokens
{
    private static readonly Dictionary<AuditAction, string> Tokens = new()
    {
        [AuditAction.SubjectCreated] = "SUBJECT_CREATED",
        [AuditAction.SubjectUpdated] = "SUBJECT_UPDATED",
        [AuditAction.SubjectRetired] = "SUBJECT_RETIRED",
        [AuditAction.VersionRegistered] = "VERSION_REGISTERED",
        [AuditAction.VersionSubmitted] = "VERSION_SUBMITTED",
        [AuditAction.VersionApproved] = "VERSION_APPROVED",
        [AuditAction.VersionRejected] = "VERSION_REJECTED",
        [AuditAction.VersionDismissed] = "VERSION_DISMISSED",
        [AuditAction.VersionPromoted] = "VERSION_PROMOTED",
        [AuditAction.EnvironmentCreated] = "ENVIRONMENT_CREATED",
        [AuditAction.EnvironmentUpdated] = "ENVIRONMENT_UPDATED",
        [AuditAction.BrokerAdded] = "BROKER_ADDED",
        [AuditAction.BrokerRemoved] = "BROKER_REMOVED",
        [AuditAction.BrokerCredentialSet] = "BROKER_CREDENTIAL_SET",
        [AuditAction.BrokerCredentialRemoved] = "BROKER_CREDENTIAL_REMOVED",
        [AuditAction.ContractCreated] = "CONTRACT_CREATED",
        [AuditAction.ContractBindingAdded] = "CONTRACT_BINDING_ADDED",
        [AuditAction.ContractEnforcementChanged] = "CONTRACT_ENFORCEMENT_CHANGED",
        [AuditAction.ServiceRegistered] = "SERVICE_REGISTERED",
        [AuditAction.MemberAdded] = "MEMBER_ADDED",
        [AuditAction.MemberRoleChanged] = "MEMBER_ROLE_CHANGED",
        [AuditAction.ApiKeyIssued] = "API_KEY_ISSUED",
        [AuditAction.ApiKeyRevoked] = "API_KEY_REVOKED",
    };

    private static readonly Dictionary<string, AuditAction> Actions =
        Tokens.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Maps an action to its wire token.</summary>
    /// <param name="action">The action.</param>
    /// <returns>The stable token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The action is not a known member.</exception>
    public static string For(AuditAction action) =>
        Tokens.TryGetValue(action, out var token)
            ? token
            : throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown audit action.");

    /// <summary>Parses a wire token.</summary>
    /// <param name="token">The token.</param>
    /// <param name="action">The action, when it parsed.</param>
    /// <returns>Success, or a failure naming what was expected.</returns>
    public static Result Parse(string? token, out AuditAction? action)
    {
        action = null;

        if (string.IsNullOrWhiteSpace(token))
        {
            return Result.Success();
        }

        if (!Actions.TryGetValue(token.Trim(), out var parsed))
        {
            return Result.Failure(
                ConcordatCodes.AuditFilterInvalid,
                $"Unknown audit action '{token}'. Expected one of: {string.Join(", ", Tokens.Values)}.");
        }

        action = parsed;
        return Result.Success();
    }
}
