using Concordat.Domain.Registry;

namespace Concordat.Domain.Governance;

/// <summary>
/// Something the <em>operator</em> did, as distinct from something an organisation did.
/// </summary>
/// <remarks>
/// Deliberately a short list. Anything an organisation can do to itself belongs in
/// <see cref="AuditAction"/> and its tenant-scoped trail; this is only for events that happen
/// above a tenant or before one exists.
/// </remarks>
public enum DeploymentAction
{
    /// <summary>An organisation was created through signup (M9.2).</summary>
    OrganisationCreated,

    /// <summary>A self-hosted deployment was claimed by its first account (M8.2).</summary>
    InstanceClaimed,
}

/// <summary>
/// The deployment-level trail: what happened above the tenant line (decision 29).
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from <see cref="AuditEntry"/> on purpose, and the alternative was tempting.</b>
/// The obvious fix for "a signup writes no audit entry" is to let the audit log take an explicit
/// tenant, since the only thing stopping it is that <c>StampTenant</c> uses the ambient scope and
/// at signup nobody has authenticated. That would work, and it would make cross-tenant audit
/// writes possible from anywhere — a capability worth not having by accident.
/// </para>
/// <para>
/// The deeper reason is that these are not the same kind of record. Signup, and eventually
/// suspension and billing, are things the <em>operator</em> did; they want a different retention
/// and a different reader from "who changed this subject". Bending the tenant-scoped trail to
/// hold them is how it stops being the thing people trust.
/// </para>
/// <para>
/// <b>Not tenant-filtered, which is exactly why it is not readable over HTTP yet.</b> There is
/// no operator role to gate an endpoint with: in a self-hosted deployment the instance owner is
/// the operator, and in Cloud they emphatically are not. Gating on <c>org:admin</c> would be
/// right in one profile and a cross-tenant disclosure in the other. Until that role exists the
/// rows are written and read out of band.
/// </para>
/// </remarks>
public sealed class DeploymentEvent
{
    /// <summary>The longest actor string kept.</summary>
    public const int MaxActorLength = 320;

    /// <summary>The longest detail kept.</summary>
    public const int MaxDetailLength = 1024;

    // A private constructor with get-only properties rather than a positional record, matching
    // AuditEntry. EF binds constructor parameters to mapped properties by name, and a nullable
    // value-type identity behind a value converter cannot be bound that way -- the design-time
    // model build fails outright with "cannot bind 'TenantId'". Worth stating because the
    // positional form reads better and does not work.
    private DeploymentEvent(
        Guid id,
        DateTimeOffset occurredAt,
        DeploymentAction action,
        string actor,
        TenantId? tenantId,
        string detail)
    {
        Id = id;
        OccurredAt = occurredAt;
        Action = action;
        Actor = actor;
        TenantId = tenantId;
        Detail = detail;
    }

    /// <summary>Time-ordered, so the table sorts by insertion without a second index.</summary>
    public Guid Id { get; }

    /// <summary>When it happened.</summary>
    public DateTimeOffset OccurredAt { get; }

    /// <summary>What happened.</summary>
    public DeploymentAction Action { get; }

    /// <summary>
    /// Who did it, as they identified themselves. An email at signup, since no account exists yet.
    /// </summary>
    public string Actor { get; private set; } = null!;

    /// <summary>
    /// The organisation the event concerns, <b>as data rather than as scope</b>.
    /// </summary>
    /// <remarks>
    /// This row is not filtered by it and does not belong to that organisation's trail. The
    /// distinction is the whole reason the table exists.
    /// </remarks>
    public TenantId? TenantId { get; }

    /// <summary>A human-readable summary.</summary>
    public string Detail { get; private set; } = null!;

    /// <summary>Records an event.</summary>
    /// <param name="action">What happened.</param>
    /// <param name="actor">Who did it.</param>
    /// <param name="tenantId">The organisation it concerns, when there is one.</param>
    /// <param name="detail">A summary.</param>
    /// <param name="at">When.</param>
    /// <returns>The event.</returns>
    /// <remarks>
    /// <b>Truncates rather than refuses</b>, matching <see cref="AuditEntry"/>. A trail that can
    /// reject its own entries is a trail with holes in it at exactly the moments worth recording.
    /// </remarks>
    public static DeploymentEvent Record(
        DeploymentAction action,
        string? actor,
        TenantId? tenantId,
        string? detail,
        DateTimeOffset at) =>
        new(
            Guid.CreateVersion7(),
            at.ToUniversalTime(),
            action,
            Trim(actor, MaxActorLength, "unknown"),
            tenantId,
            Trim(detail, MaxDetailLength, string.Empty));

    private static string Trim(string? value, int max, string fallback)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return fallback;
        }

        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

/// <summary>Wire spellings for <see cref="DeploymentAction"/>.</summary>
/// <remarks>
/// Explicit, never <c>ToUpperInvariant()</c> on the member name — the class of bug M6.1 found
/// across the API, where <c>CiOnly</c> would have serialised as <c>CIONLY</c>.
/// </remarks>
public static class DeploymentTokens
{
    /// <summary>The token for an action.</summary>
    /// <param name="action">The action.</param>
    /// <returns>The stable token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The action is not a known member.</exception>
    public static string For(DeploymentAction action) => action switch
    {
        DeploymentAction.OrganisationCreated => "ORGANISATION_CREATED",
        DeploymentAction.InstanceClaimed => "INSTANCE_CLAIMED",
        _ => throw new ArgumentOutOfRangeException(
            nameof(action),
            action,
            $"Unknown deployment action '{action}'."),
    };
}
