using Concordat.Domain.Results;

namespace Concordat.Domain.Identity;

/// <summary>
/// What a member may do (ADR-018).
/// </summary>
/// <remarks>
/// Three roles, not a permission matrix. A registry with per-subject grants is a reasonable
/// future refinement — <c>Subject.Owner</c> already exists for it — but shipping one before
/// anyone has enough subjects to need it means every install pays for a model nobody uses, and
/// the interesting question (can this person change a contract?) gets a longer answer than it
/// deserves.
/// </remarks>
public enum Role
{
    /// <summary>
    /// The complete read surface and nothing else. The default, and the majority.
    /// </summary>
    /// <remarks>
    /// ADR-018: the people who need to read the registry vastly outnumber the people who
    /// should be able to change it, so reading is what a membership grants by default.
    /// </remarks>
    Reader = 1,

    /// <summary>Everything a Reader may do, plus every schema, contract and broker write.</summary>
    Admin = 2,

    /// <summary>An Admin who may also manage members, roles and API keys.</summary>
    Owner = 3,
}

/// <summary>Maps roles to the scopes they grant, and both to their wire tokens.</summary>
public static class Roles
{
    private static readonly string[] ReaderScopes =
    [
        Scope.SubjectRead,
        Scope.ContractRead,
        Scope.EnvRead,
        Scope.BrokerRead,
    ];

    private static readonly string[] AdminScopes =
    [
        Scope.SubjectAdmin,
        Scope.ContractWrite,
        Scope.EnvWrite,
        Scope.BrokerWrite,
    ];

    private static readonly string[] OwnerScopes = [.. AdminScopes, Scope.OrgAdmin];

    /// <summary>The scopes a role grants, before implications are expanded.</summary>
    /// <param name="role">The role.</param>
    /// <returns>The granted scopes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The role is not a known member.</exception>
    public static IReadOnlyList<string> ScopesFor(Role role) => role switch
    {
        Role.Reader => ReaderScopes,
        Role.Admin => AdminScopes,
        Role.Owner => OwnerScopes,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role."),
    };

    /// <summary>The scope set a role resolves to, with implications expanded.</summary>
    /// <param name="role">The role.</param>
    /// <returns>The set.</returns>
    public static ScopeSet SetFor(Role role) => ScopeSet.Of(ScopesFor(role));

    /// <summary>Maps a role to its wire token.</summary>
    /// <param name="role">The role.</param>
    /// <returns>The stable token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The role is not a known member.</exception>
    public static string For(Role role) => role switch
    {
        Role.Reader => "READER",
        Role.Admin => "ADMIN",
        Role.Owner => "OWNER",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role."),
    };

    /// <summary>Parses a wire token.</summary>
    /// <param name="token">The token.</param>
    /// <param name="role">The role, when it parsed.</param>
    /// <returns>Success, or a failure naming what was expected.</returns>
    public static Result Parse(string? token, out Role role)
    {
        switch (token?.Trim().ToUpperInvariant())
        {
            case "READER":
                role = Role.Reader;
                return Result.Success();
            case "ADMIN":
                role = Role.Admin;
                return Result.Success();
            case "OWNER":
                role = Role.Owner;
                return Result.Success();
            default:
                role = Role.Reader;
                return Result.Failure(
                    ConcordatCodes.RoleInvalid,
                    $"Unknown role '{token}'. Expected READER, ADMIN or OWNER.");
        }
    }
}
