using System.Text.RegularExpressions;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Domain.Identity;

/// <summary>
/// An organisation, and the isolation boundary every row sits inside (M9.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Self-hosted has exactly one, and it is not special-cased anywhere above this type.</b>
/// The tenant column, the query filters and the membership model have been in place since
/// M1.5 precisely so that Cloud is a different <c>ITenantContext</c> registration at the
/// composition root rather than a second code path — DESIGN §8's whole argument.
/// </para>
/// <para>
/// The row exists in both flavours. A self-hosted install gets one named after itself on
/// first run, so that <c>membership.tenant_id</c> points at something real and the two
/// profiles differ in how many rows there are, not in whether the table means anything.
/// </para>
/// </remarks>
public sealed partial class Tenant
{
    /// <summary>The longest permitted display name, in characters.</summary>
    public const int MaxNameLength = 128;

    /// <summary>The longest permitted slug, in characters.</summary>
    public const int MaxSlugLength = 63;

    private Tenant(TenantId id, string name, string slug, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        Slug = slug;
        CreatedAt = createdAt;
    }

    // Materialisation only.
    private Tenant()
    {
        Name = null!;
        Slug = null!;
    }

    /// <summary>The identity every tenant-scoped row carries.</summary>
    public TenantId Id { get; }

    /// <summary>What the organisation calls itself.</summary>
    public string Name { get; private set; }

    /// <summary>
    /// The URL-safe handle, unique across the deployment.
    /// </summary>
    /// <remarks>
    /// Capped at 63 characters because a slug becomes a DNS label in Cloud
    /// (<c>acme.concordat.dev</c>), and a name that cannot be a hostname is one somebody
    /// discovers at the point of provisioning rather than at the point of signup.
    /// </remarks>
    public string Slug { get; private set; }

    /// <summary>When the organisation was created.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Creates an organisation.</summary>
    /// <param name="name">What it calls itself.</param>
    /// <param name="slug">The URL-safe handle.</param>
    /// <param name="createdAt">When.</param>
    /// <param name="id">An explicit identity, or null to mint one.</param>
    /// <returns>The tenant, or a validation failure.</returns>
    public static Result<Tenant> Create(
        string? name, string? slug, DateTimeOffset createdAt, TenantId? id = null)
    {
        var displayName = name?.Trim();

        if (string.IsNullOrEmpty(displayName))
        {
            return Result<Tenant>.Failure(
                ConcordatCodes.TenantNameInvalid, "An organisation name is required.");
        }

        if (displayName.Length > MaxNameLength)
        {
            return Result<Tenant>.Failure(
                ConcordatCodes.TenantNameInvalid,
                $"An organisation name may be at most {MaxNameLength} characters.");
        }

        var handle = (slug ?? displayName).Trim().ToLowerInvariant();

        if (handle.Length > MaxSlugLength)
        {
            return Result<Tenant>.Failure(
                ConcordatCodes.TenantSlugInvalid,
                $"A slug may be at most {MaxSlugLength} characters, because it becomes a DNS " +
                "label.");
        }

        return SlugPattern().IsMatch(handle)
            ? Result<Tenant>.Success(new Tenant(
                id ?? TenantId.New(), displayName, handle, createdAt.ToUniversalTime()))
            : Result<Tenant>.Failure(
                ConcordatCodes.TenantSlugInvalid,
                $"'{handle}' is not a usable slug. Use lowercase letters, digits and hyphens, " +
                "starting and ending with a letter or digit.");
    }

    /// <summary>Changes the display name. The slug is immutable — it is in URLs.</summary>
    /// <param name="name">The new name.</param>
    /// <returns>Success, or a validation failure.</returns>
    public Result Rename(string? name)
    {
        var displayName = name?.Trim();

        if (string.IsNullOrEmpty(displayName) || displayName.Length > MaxNameLength)
        {
            return Result.Failure(
                ConcordatCodes.TenantNameInvalid,
                $"An organisation name is required and may be at most {MaxNameLength} " +
                "characters.");
        }

        Name = displayName;
        return Result.Success();
    }

    // A DNS label: lowercase alphanumerics and hyphens, not starting or ending with one.
    [GeneratedRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
