using Concordat.Domain.Results;

namespace Concordat.Domain.Registry;

/// <summary>
/// A deployment environment: a logical label over registered brokers (ADR-012).
/// </summary>
/// <remarks>
/// <para>
/// <b>The label is primary, the brokers are attached to it.</b> ADR-012 chose this over
/// modelling brokers as the top-level thing because a contract is promoted between
/// <em>environments</em> — <c>dev → staging → prod</c> — and that sentence has no meaning if
/// the unit is a host. It also survives the estate changing shape underneath it: moving
/// <c>prod</c> to a new cluster is a broker edit, not a re-registration of every subject.
/// </para>
/// <para>
/// <b>The type is deliberately named <c>Environment</c>, shadowing <c>System.Environment</c>
/// inside this namespace.</b> The domain language, every ADR, and the URL
/// <c>/v1/environments/{env}</c> all say "environment"; renaming the type to dodge a BCL
/// collision would leave the code speaking a different language from its own documentation.
/// Files needing both qualify the BCL one.
/// </para>
/// <para>
/// <b>Until M7 this aggregate did not exist</b>, and <c>DerivedEnvironmentResolver</c> hashed
/// the name to a stable id so the routes worked anyway. Rows created by that scheme keep their
/// ids — see the M7.1 migration — because the alternative was rewriting <c>environment_id</c>
/// on every subject ever registered.
/// </para>
/// </remarks>
public sealed class Environment
{
    private readonly List<BrokerConnection> _brokers;

    private Environment(
        EnvironmentId id,
        EnvironmentName name,
        string? description,
        CompatibilityPolicy defaultCompatibilityPolicy,
        RegistrationPolicy registrationPolicy,
        DateTimeOffset createdAt,
        List<BrokerConnection> brokers)
    {
        Id = id;
        Name = name;
        Description = description;
        DefaultCompatibilityPolicy = defaultCompatibilityPolicy;
        RegistrationPolicy = registrationPolicy;
        CreatedAt = createdAt;
        _brokers = brokers;
    }

    // Materialisation only. EF cannot bind an owned collection through a constructor.
    private Environment()
    {
        _brokers = [];
        Name = null!;
    }

    /// <summary>The surrogate identity.</summary>
    public EnvironmentId Id { get; }

    /// <summary>The name that appears in every route.</summary>
    /// <remarks>
    /// Settable only at creation. The id is what subjects reference, so a rename would not
    /// orphan anything — but the name is in every pipeline's configuration and every URL
    /// anyone has bookmarked, and there is no version of "we renamed prod" that ends well.
    /// </remarks>
    public EnvironmentName Name { get; }

    /// <summary>What this environment is for.</summary>
    public string? Description { get; private set; }

    /// <summary>
    /// The policy a subject inherits when it declares none of its own.
    /// </summary>
    /// <remarks>
    /// Per environment because the answer differs by environment: <c>prod</c> wants the strict
    /// pair, and a scratch environment that blocked every experiment would just be switched off.
    /// <see cref="Subject.CompatibilityPolicy"/> stays nullable so that "inheriting" and
    /// "explicitly set to the same value" remain distinguishable.
    /// </remarks>
    public CompatibilityPolicy DefaultCompatibilityPolicy { get; private set; }

    /// <summary>Whether an SDK may register schemas directly against this environment.</summary>
    /// <remarks>
    /// <b>Enforced server-side, which is the whole point.</b> Confluent's equivalent is
    /// client-side only with no server override, so one misconfigured producer in any language
    /// permanently pollutes the registry. Defaults to <see cref="RegistrationPolicy.CiOnly"/>
    /// for anything named like a production environment — see <see cref="Create"/>.
    /// </remarks>
    public RegistrationPolicy RegistrationPolicy { get; private set; }

    /// <summary>When the environment was created.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>The registered brokers.</summary>
    /// <remarks>
    /// Wrapped rather than returned directly: an <see cref="IReadOnlyList{T}"/> backed by a
    /// <see cref="List{T}"/> can be cast back to <see cref="ICollection{T}"/> and mutated,
    /// bypassing the duplicate-endpoint invariant.
    /// </remarks>
    public IReadOnlyList<BrokerConnection> Brokers => _brokers.AsReadOnly();

    /// <summary>Creates an environment.</summary>
    /// <param name="name">The name.</param>
    /// <param name="createdAt">When it was created.</param>
    /// <param name="description">What it is for.</param>
    /// <param name="defaultCompatibilityPolicy">
    /// The policy subjects inherit, or null for <see cref="CompatibilityPolicy.Default"/>.
    /// </param>
    /// <param name="registrationPolicy">
    /// Who may register, or null to choose by name — see the remarks.
    /// </param>
    /// <param name="id">
    /// An explicit id, used only by the migration that adopts derived ids. Omit it otherwise.
    /// </param>
    /// <returns>The environment, or the first validation failure.</returns>
    /// <remarks>
    /// <b>An environment named like production defaults to <see cref="RegistrationPolicy.CiOnly"/>.</b>
    /// A default that is safe everywhere is the wrong default somewhere, and the asymmetry here
    /// is stark: an over-permissive <c>prod</c> is polluted silently and permanently, while an
    /// over-strict scratch environment produces one clear error and a config change. Guessing
    /// from the name is crude, and it is still better than defaulting <c>prod</c> to open.
    /// </remarks>
    public static Result<Environment> Create(
        string? name,
        DateTimeOffset createdAt,
        string? description = null,
        CompatibilityPolicy? defaultCompatibilityPolicy = null,
        RegistrationPolicy? registrationPolicy = null,
        EnvironmentId? id = null)
    {
        var parsed = EnvironmentName.Create(name);
        if (parsed.IsFailure)
        {
            return Result<Environment>.Failure(parsed.Error!);
        }

        var trimmedDescription = description?.Trim();
        if (trimmedDescription is { Length: > 512 })
        {
            return Result<Environment>.Failure(
                ConcordatCodes.EnvironmentNameInvalid,
                "An environment description may be at most 512 characters.");
        }

        return Result<Environment>.Success(new Environment(
            id ?? EnvironmentId.New(),
            parsed.Value,
            string.IsNullOrEmpty(trimmedDescription) ? null : trimmedDescription,
            defaultCompatibilityPolicy ?? CompatibilityPolicy.Default,
            registrationPolicy ?? DefaultRegistrationPolicyFor(parsed.Value),
            createdAt.ToUniversalTime(),
            []));
    }

    /// <summary>Adds a broker.</summary>
    /// <param name="broker">The connection.</param>
    /// <returns>
    /// Success, or a failure carrying <see cref="ConcordatCodes.BrokerAlreadyExists"/>.
    /// </returns>
    /// <remarks>
    /// The identity of a connection is <c>(host, port, virtual host)</c>, not the display name
    /// and not the URI alone. DESIGN §4's own example registers one host twice under different
    /// virtual hosts, so rejecting a repeated host would refuse a documented topology.
    /// </remarks>
    public Result AddBroker(BrokerConnection broker)
    {
        ArgumentNullException.ThrowIfNull(broker);

        if (_brokers.Any(b => SameEndpoint(b, broker)))
        {
            return Result.Failure(
                ConcordatCodes.BrokerAlreadyExists,
                $"A broker for '{broker.Uri.Authority}' on virtual host " +
                $"'{broker.VirtualHost}' is already registered in this environment.");
        }

        if (_brokers.Any(b => string.Equals(
                b.DisplayName, broker.DisplayName, StringComparison.OrdinalIgnoreCase)))
        {
            return Result.Failure(
                ConcordatCodes.BrokerAlreadyExists,
                $"A broker named '{broker.DisplayName}' is already registered in this " +
                "environment. Names are how operators tell them apart.");
        }

        _brokers.Add(broker);
        return Result.Success();
    }

    /// <summary>Removes a broker.</summary>
    /// <param name="brokerId">Its identity.</param>
    /// <returns>
    /// Success, or a failure carrying <see cref="ConcordatCodes.BrokerNotFound"/>.
    /// </returns>
    public Result RemoveBroker(Guid brokerId)
    {
        var broker = _brokers.FirstOrDefault(b => b.Id == brokerId);
        if (broker is null)
        {
            return Result.Failure(
                ConcordatCodes.BrokerNotFound, "No broker with that id in this environment.");
        }

        _brokers.Remove(broker);
        return Result.Success();
    }

    /// <summary>Finds a broker by id.</summary>
    /// <param name="brokerId">Its identity.</param>
    /// <returns>The broker, or null.</returns>
    public BrokerConnection? Broker(Guid brokerId) =>
        _brokers.FirstOrDefault(b => b.Id == brokerId);

    /// <summary>Changes the description.</summary>
    /// <param name="description">The new description, or null to clear it.</param>
    public void Describe(string? description)
    {
        var trimmed = description?.Trim();
        Description = string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>Changes the policy subjects inherit.</summary>
    /// <param name="policy">The new default.</param>
    /// <remarks>
    /// Takes effect immediately for every subject that has not set its own, which is the point
    /// of inheritance — and worth knowing before changing it on an environment with traffic.
    /// </remarks>
    public void SetDefaultCompatibilityPolicy(CompatibilityPolicy policy) =>
        DefaultCompatibilityPolicy = policy;

    /// <summary>Changes who may register schemas here.</summary>
    /// <param name="policy">The new policy.</param>
    public void SetRegistrationPolicy(RegistrationPolicy policy) => RegistrationPolicy = policy;

    private static bool SameEndpoint(BrokerConnection left, BrokerConnection right) =>
        string.Equals(left.Uri.Authority, right.Uri.Authority, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.VirtualHost, right.VirtualHost, StringComparison.Ordinal);

    private static RegistrationPolicy DefaultRegistrationPolicyFor(EnvironmentName name) =>
        name.Value is "prod" or "production" or "live"
            ? RegistrationPolicy.CiOnly
            : RegistrationPolicy.Open;
}
