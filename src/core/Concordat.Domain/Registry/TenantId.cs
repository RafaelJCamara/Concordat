namespace Concordat.Domain.Registry;

/// <summary>
/// The tenant that owns a row.
/// </summary>
/// <param name="Value">The underlying identifier.</param>
/// <remarks>
/// <para>
/// Tenancy is a Cloud concern (M9), but the isolation mechanism is wired from M1.5 with a
/// single implicit tenant. Adding a discriminator to populated tables later means a data
/// migration and an audit of every query that was written without one; adding it now costs a
/// column nobody looks at.
/// </para>
/// <para>
/// Not every table is tenant-scoped. Schemas are global and deduplicated by content
/// (ADR-015), so <see cref="Schema"/> deliberately carries no tenant.
/// </para>
/// </remarks>
public readonly record struct TenantId(Guid Value)
{
    /// <summary>
    /// The tenant every row belongs to in a self-hosted deployment.
    /// </summary>
    /// <remarks>
    /// A fixed, non-empty value rather than <see cref="Guid.Empty"/>: an all-zero identifier
    /// is indistinguishable from an uninitialised one, so a row that lost its tenant would
    /// look like it belonged to the default rather than looking wrong.
    /// </remarks>
    public static TenantId SelfHosted { get; } = new(new Guid("00000000-0000-0000-0000-00000000c0de"));

    /// <summary>Creates a new identifier.</summary>
    /// <returns>A fresh <see cref="TenantId"/>.</returns>
    public static TenantId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}
