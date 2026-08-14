using Concordat.Domain.Results;

namespace Concordat.Domain.Identity;

/// <summary>
/// The authorisation vocabulary (DESIGN §4 Context E, ADR-008).
/// </summary>
/// <remarks>
/// <para>
/// These strings are protocol. They are returned to the web application at sign-in, written
/// into API keys, and compared server-side on every mutating endpoint — the same list appears
/// in <c>web/src/app/domain/identity/scope.ts</c> and the two must not drift.
/// </para>
/// <para>
/// <b>Held as constants rather than an enum</b> because a scope is a pair — a resource and a
/// level — and the implication rules below are about that structure. An enum would make
/// <c>subject:admin</c> and <c>subject:read</c> unrelated values that happen to sort near each
/// other.
/// </para>
/// </remarks>
public static class Scope
{
    /// <summary>Read subjects, versions and schemas.</summary>
    public const string SubjectRead = "subject:read";

    /// <summary>Create subjects and register versions (ADR-018: admin roles only).</summary>
    public const string SubjectWrite = "subject:write";

    /// <summary>Approve, reject, retire and promote (ADR-018: admin roles only).</summary>
    public const string SubjectAdmin = "subject:admin";

    /// <summary>Read contracts and resolve topologies.</summary>
    public const string ContractRead = "contract:read";

    /// <summary>Create contracts and add bindings.</summary>
    public const string ContractWrite = "contract:write";

    /// <summary>Read environments.</summary>
    public const string EnvRead = "env:read";

    /// <summary>Create and change environments.</summary>
    public const string EnvWrite = "env:write";

    /// <summary>Read brokers, without their credentials.</summary>
    public const string BrokerRead = "broker:read";

    /// <summary>Add brokers and set their credentials.</summary>
    public const string BrokerWrite = "broker:write";

    /// <summary>Manage members, roles and API keys.</summary>
    public const string OrgAdmin = "org:admin";

    /// <summary>Every scope, in the order the frontend lists them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        SubjectRead,
        SubjectWrite,
        SubjectAdmin,
        ContractRead,
        ContractWrite,
        EnvRead,
        EnvWrite,
        BrokerRead,
        BrokerWrite,
        OrgAdmin,
    ];

    /// <summary>
    /// What holding one scope also grants.
    /// </summary>
    /// <remarks>
    /// <b>Enumerated, never derived from the string.</b> A prefix or <c>StartsWith</c> rule
    /// would make a future <c>subject:read-only</c> silently satisfy a <c>subject:read</c>
    /// check, and a typo in a stored key would grant more than it names. Every implication
    /// here is one somebody wrote down.
    /// </remarks>
    private static readonly Dictionary<string, string[]> Implies = new(StringComparer.Ordinal)
    {
        [SubjectAdmin] = [SubjectWrite, SubjectRead],
        [SubjectWrite] = [SubjectRead],
        [ContractWrite] = [ContractRead],
        [EnvWrite] = [EnvRead],
        [BrokerWrite] = [BrokerRead],

        // org:admin is membership and key management. It deliberately does NOT imply
        // subject:write: an owner who has not granted themselves schema rights should not
        // acquire them by managing the org, because ADR-018's whole point is that the set of
        // people who can change a contract stays small and deliberate.
        [OrgAdmin] = [],
    };

    /// <summary>Whether a token is a scope this build knows.</summary>
    /// <param name="scope">The token.</param>
    /// <returns>True when it is one of <see cref="All"/>.</returns>
    public static bool IsKnown(string? scope) =>
        scope is not null && All.Contains(scope, StringComparer.Ordinal);

    /// <summary>Expands a granted scope to everything it implies, including itself.</summary>
    /// <param name="scope">The granted scope.</param>
    /// <returns>The scope and everything below it.</returns>
    public static IReadOnlyList<string> Expand(string scope)
    {
        if (!IsKnown(scope))
        {
            return [];
        }

        return Implies.TryGetValue(scope, out var implied) ? [scope, .. implied] : [scope];
    }

    /// <summary>Parses and validates a set of scope tokens.</summary>
    /// <param name="tokens">The tokens.</param>
    /// <param name="scopes">The validated set, expanded to include implications.</param>
    /// <returns>Success, or a failure naming the first unknown token.</returns>
    public static Result Parse(IReadOnlyList<string>? tokens, out IReadOnlyList<string> scopes)
    {
        scopes = [];

        if (tokens is null || tokens.Count is 0)
        {
            return Result.Failure(
                ConcordatCodes.ScopeInvalid,
                "At least one scope is required. A credential that grants nothing is a " +
                "credential nobody can use, which reads as a bug rather than a decision.");
        }

        var expanded = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var token in tokens)
        {
            var trimmed = token?.Trim();

            if (!IsKnown(trimmed))
            {
                return Result.Failure(
                    ConcordatCodes.ScopeInvalid,
                    $"'{token}' is not a scope. Expected one of: {string.Join(", ", All)}.");
            }

            foreach (var scope in Expand(trimmed!))
            {
                expanded.Add(scope);
            }
        }

        scopes = [.. expanded];
        return Result.Success();
    }
}

/// <summary>
/// What a caller is allowed to do.
/// </summary>
/// <remarks>
/// Constructed once per request from a session or an API key, and consulted by every mutating
/// endpoint. It holds scopes already expanded through <see cref="Scope.Expand"/>, so
/// <see cref="Allows"/> is a set lookup rather than a rules engine that could be asked the same
/// question two ways and answer differently.
/// </remarks>
public sealed class ScopeSet
{
    private readonly HashSet<string> _scopes;

    private ScopeSet(HashSet<string> scopes) => _scopes = scopes;

    /// <summary>A caller with no scopes at all.</summary>
    public static ScopeSet None { get; } = new([]);

    /// <summary>The granted scopes, expanded and ordered.</summary>
    public IReadOnlyList<string> Granted => [.. _scopes.Order(StringComparer.Ordinal)];

    /// <summary>Builds a set from already-validated tokens, expanding implications.</summary>
    /// <param name="scopes">The granted scopes.</param>
    /// <returns>The set.</returns>
    public static ScopeSet Of(IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        var expanded = new HashSet<string>(StringComparer.Ordinal);

        foreach (var scope in scopes)
        {
            foreach (var implied in Scope.Expand(scope))
            {
                expanded.Add(implied);
            }
        }

        return new ScopeSet(expanded);
    }

    /// <summary>Whether this caller holds a scope.</summary>
    /// <param name="required">The scope an endpoint requires.</param>
    /// <returns>True when the caller may proceed.</returns>
    public bool Allows(string required) => _scopes.Contains(required);

    /// <summary>Whether this caller holds any of several scopes.</summary>
    /// <param name="required">The alternatives.</param>
    /// <returns>True when the caller holds at least one.</returns>
    public bool AllowsAny(params string[] required)
    {
        ArgumentNullException.ThrowIfNull(required);

        return required.Any(_scopes.Contains);
    }
}
