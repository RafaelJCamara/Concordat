using Concordat.Domain.Results;

namespace Concordat.Domain.Billing;

/// <summary>What a tenant is paying for (M9.3).</summary>
public enum Tier
{
    /// <summary>Enough to evaluate the product properly and not enough to run a company on.</summary>
    Free = 0,

    /// <summary>One team.</summary>
    Team = 1,

    /// <summary>Several teams, and the governance surface that implies.</summary>
    Business = 2,

    /// <summary>Negotiated. Limits are configured rather than fixed.</summary>
    Enterprise = 3,
}

/// <summary>
/// What a tier allows.
/// </summary>
/// <param name="Tier">The tier.</param>
/// <param name="Environments">The most environments, or null for no limit.</param>
/// <param name="Subjects">The most subjects, or null for no limit.</param>
/// <param name="Seats">The most members, or null for no limit.</param>
/// <param name="VersionsPerMonth">The most versions registered per calendar month, or null.</param>
/// <remarks>
/// <b>Null means unlimited, and zero would have been a trap.</b> A limit of zero is a real,
/// meaningful value — "you may create none of these" — so using it for "no limit" would make
/// the most restrictive plan indistinguishable from the least.
/// </remarks>
public sealed record PlanLimits(
    Tier Tier,
    int? Environments,
    int? Subjects,
    int? Seats,
    int? VersionsPerMonth)
{
    /// <summary>The limits each tier carries.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>Its limits.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The tier is not a known member.</exception>
    /// <remarks>
    /// Held in code rather than configuration because a limit that can be edited per deployment
    /// is a limit no invoice can be reconciled against. Enterprise is the exception, and it is
    /// unlimited here precisely so that "negotiated" does not mean "someone typed a number into
    /// a config file".
    /// </remarks>
    public static PlanLimits For(Tier tier) => tier switch
    {
        Tier.Free => new PlanLimits(tier, Environments: 1, Subjects: 10, Seats: 3, VersionsPerMonth: 100),
        Tier.Team => new PlanLimits(tier, Environments: 3, Subjects: 100, Seats: 15, VersionsPerMonth: 1_000),
        Tier.Business => new PlanLimits(tier, Environments: 10, Subjects: 1_000, Seats: 100, VersionsPerMonth: 10_000),
        Tier.Enterprise => new PlanLimits(tier, null, null, null, null),
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown tier."),
    };

    /// <summary>Everything unlimited, for a deployment that does not bill.</summary>
    /// <remarks>
    /// What self-hosted uses. Not <see cref="Tier.Enterprise"/>: a self-hosted install has no
    /// subscription at all, and labelling it as the most expensive tier would put a
    /// commercial-sounding word in front of somebody who is not a customer.
    /// </remarks>
    public static PlanLimits Unlimited { get; } =
        new(Tier.Enterprise, null, null, null, null);
}

/// <summary>The wire spellings of <see cref="Tier"/>.</summary>
public static class Tiers
{
    /// <summary>Maps a tier to its wire token.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The stable token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The tier is not a known member.</exception>
    public static string For(Tier tier) => tier switch
    {
        Tier.Free => "FREE",
        Tier.Team => "TEAM",
        Tier.Business => "BUSINESS",
        Tier.Enterprise => "ENTERPRISE",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown tier."),
    };

    /// <summary>Parses a wire token.</summary>
    /// <param name="token">The token.</param>
    /// <param name="tier">The tier, when it parsed.</param>
    /// <returns>Success, or a failure naming what was expected.</returns>
    public static Result Parse(string? token, out Tier tier)
    {
        switch (token?.Trim().ToUpperInvariant())
        {
            case "FREE":
                tier = Tier.Free;
                return Result.Success();
            case "TEAM":
                tier = Tier.Team;
                return Result.Success();
            case "BUSINESS":
                tier = Tier.Business;
                return Result.Success();
            case "ENTERPRISE":
                tier = Tier.Enterprise;
                return Result.Success();
            default:
                // The least privileged tier on failure, for the same reason Roles.Parse returns
                // Reader: a caller that ignores the Result must not be handed the best plan.
                tier = Tier.Free;
                return Result.Failure(
                    ConcordatCodes.TierInvalid,
                    $"Unknown tier '{token}'. Expected FREE, TEAM, BUSINESS or ENTERPRISE.");
        }
    }
}
