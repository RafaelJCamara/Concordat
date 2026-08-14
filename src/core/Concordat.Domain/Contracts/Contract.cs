using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Domain.Contracts;

/// <summary>
/// Where a binding applies: one virtual host on one broker.
/// </summary>
/// <param name="BrokerId">The broker, or null for every broker in the environment.</param>
/// <param name="VirtualHost">The virtual host.</param>
/// <remarks>
/// A null <see cref="BrokerId"/> means "wherever this environment publishes", which is the
/// common case and the one worth making easy: most estates have one topology per environment
/// and naming a broker in every binding would be noise that drifts when the broker is replaced.
/// </remarks>
public sealed record TopologyScope(Guid? BrokerId, string VirtualHost)
{
    /// <summary>Every broker in the environment, on the default virtual host.</summary>
    public static TopologyScope Default { get; } = new(null, "/");

    /// <summary>Whether this scope covers a concrete broker and virtual host.</summary>
    /// <param name="brokerId">The broker a message is going through.</param>
    /// <param name="virtualHost">Its virtual host.</param>
    /// <returns><see langword="true"/> when the binding applies there.</returns>
    public bool Covers(Guid brokerId, string virtualHost) =>
        (BrokerId is null || BrokerId == brokerId) &&
        string.Equals(VirtualHost, virtualHost, StringComparison.Ordinal);

    /// <summary>Whether two scopes can both apply to one message.</summary>
    /// <param name="other">The other scope.</param>
    /// <returns><see langword="true"/> when they can overlap.</returns>
    public bool Overlaps(TopologyScope other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(VirtualHost, other.VirtualHost, StringComparison.Ordinal) &&
            (BrokerId is null || other.BrokerId is null || BrokerId == other.BrokerId);
    }
}

/// <summary>A publisher's contract: what may be sent to an exchange under a routing pattern.</summary>
/// <param name="Scope">Where it applies.</param>
/// <param name="Exchange">The exchange.</param>
/// <param name="RoutingKeyPattern">Which routing keys it governs.</param>
/// <param name="Subjects">The subjects permitted, and which versions.</param>
/// <param name="Precedence">
/// Which binding wins when two overlap. Null means "no precedence declared", which is what
/// makes an overlap a conflict rather than a resolution.
/// </param>
public sealed record PublishBinding(
    TopologyScope Scope,
    string Exchange,
    RoutingKeyPattern RoutingKeyPattern,
    IReadOnlyList<SubjectRef> Subjects,
    int? Precedence = null);

/// <summary>A consumer's contract: what may arrive on a queue.</summary>
/// <param name="Scope">Where it applies.</param>
/// <param name="Queue">The queue.</param>
/// <param name="Subjects">The subjects expected, and which versions.</param>
public sealed record ConsumeBinding(
    TopologyScope Scope,
    string Queue,
    IReadOnlyList<SubjectRef> Subjects);

/// <summary>
/// What a topology is contracted to carry (DESIGN §4, Context B).
/// </summary>
/// <remarks>
/// <para>
/// <b>The differentiator, and the thing a schema registry normally cannot say.</b> A registry
/// knows that <c>acme.orders.OrderCreated</c> exists and what shape it has. It does not know
/// that the <c>orders</c> exchange is supposed to carry it, so it cannot tell a publisher that
/// the message it just sent does not belong on that route — which is the failure operators
/// actually hit.
/// </para>
/// <para>
/// This exists because publisher and consumer name different things. A producer knows
/// <c>(exchange, routing key)</c>; a consumer knows <c>(queue)</c>; the binding between them is
/// declared by whoever owns the queue and can change with neither side redeploying. A contract
/// is where those two vocabularies are written down against the same subjects.
/// </para>
/// <para>
/// <b>Enforcement is per contract, and defaults to <see cref="EnforcementMode.Monitor"/>.</b>
/// A contract that started blocking the moment it was written would be authored by guessing
/// and then discovered in production; Monitor lets a team see what a contract would have
/// rejected before it rejects anything.
/// </para>
/// </remarks>
public sealed class Contract
{
    private readonly List<PublishBinding> _publishes;
    private readonly List<ConsumeBinding> _consumes;

    private Contract(
        Guid id,
        EnvironmentId environmentId,
        string name,
        EnforcementMode enforcement,
        DateTimeOffset createdAt,
        List<PublishBinding> publishes,
        List<ConsumeBinding> consumes)
    {
        Id = id;
        EnvironmentId = environmentId;
        Name = name;
        Enforcement = enforcement;
        CreatedAt = createdAt;
        _publishes = publishes;
        _consumes = consumes;
    }

    // Materialisation only.
    private Contract()
    {
        _publishes = [];
        _consumes = [];
        Name = null!;
    }

    /// <summary>The surrogate identity.</summary>
    public Guid Id { get; }

    /// <summary>The environment this contract governs.</summary>
    public EnvironmentId EnvironmentId { get; }

    /// <summary>A human name, unique within the environment.</summary>
    public string Name { get; }

    /// <summary>How much this contract may do about what it finds.</summary>
    public EnforcementMode Enforcement { get; private set; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>The publish-side bindings.</summary>
    public IReadOnlyList<PublishBinding> Publishes => _publishes.AsReadOnly();

    /// <summary>The consume-side bindings.</summary>
    public IReadOnlyList<ConsumeBinding> Consumes => _consumes.AsReadOnly();

    /// <summary>Creates a contract.</summary>
    /// <param name="environmentId">The environment.</param>
    /// <param name="name">A name unique within the environment.</param>
    /// <param name="createdAt">When.</param>
    /// <param name="enforcement">How much it may do; defaults to Monitor.</param>
    /// <returns>The contract, or a validation failure.</returns>
    public static Result<Contract> Create(
        EnvironmentId environmentId,
        string? name,
        DateTimeOffset createdAt,
        EnforcementMode? enforcement = null)
    {
        var trimmed = name?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Result<Contract>.Failure(
                ConcordatCodes.ContractNameInvalid, "A contract name is required.");
        }

        if (trimmed.Length > 128)
        {
            return Result<Contract>.Failure(
                ConcordatCodes.ContractNameInvalid,
                "A contract name may be at most 128 characters.");
        }

        return Result<Contract>.Success(new Contract(
            Guid.CreateVersion7(),
            environmentId,
            trimmed,
            enforcement ?? EnforcementMode.Monitor,
            createdAt.ToUniversalTime(),
            [],
            []));
    }

    /// <summary>Adds a publish binding.</summary>
    /// <param name="binding">The binding.</param>
    /// <returns>
    /// Success, or a failure carrying <see cref="ConcordatCodes.BindingConflict"/> when it
    /// overlaps another binding that carries different subjects with no precedence to separate
    /// them.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>The invariant DESIGN §4 asks for, and the reason it is not string equality.</b>
    /// <c>orders.*</c> and <c>*.created</c> look nothing alike and both match
    /// <c>orders.created</c>. If they name different subjects, a publisher on that key
    /// satisfies one contract and violates another, and which one it is told about depends on
    /// iteration order. Refusing the second binding is the only answer that is not arbitrary.
    /// </para>
    /// <para>
    /// <b>Precedence is the escape hatch, and it must be explicit.</b> Two overlapping bindings
    /// with distinct precedence are a deliberate specific-beats-general rule, which is a normal
    /// thing to want. Two without it are an accident.
    /// </para>
    /// <para>
    /// Overlapping bindings that carry the <em>same</em> subjects are allowed and need no
    /// precedence: they cannot disagree, so there is nothing to resolve.
    /// </para>
    /// </remarks>
    public Result AddPublish(PublishBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        foreach (var existing in _publishes)
        {
            if (!Conflicts(existing, binding))
            {
                continue;
            }

            return Result.Failure(
                ConcordatCodes.BindingConflict,
                $"'{binding.RoutingKeyPattern}' on exchange '{binding.Exchange}' overlaps " +
                $"'{existing.RoutingKeyPattern}', which carries different subjects. A key like " +
                $"'{SampleOverlap(existing, binding)}' would match both. Give the two bindings " +
                "different precedence values to say which wins, or make them carry the same " +
                "subjects.");
        }

        _publishes.Add(binding);
        return Result.Success();
    }

    /// <summary>Adds a consume binding.</summary>
    /// <param name="binding">The binding.</param>
    /// <returns>
    /// Success, or a failure when the queue is already bound to different subjects.
    /// </returns>
    /// <remarks>
    /// Simpler than the publish side, and deliberately so: a queue name is a literal, so two
    /// consume bindings either name the same queue or they do not. There is no pattern algebra
    /// and therefore no precedence — two contracts for one queue that disagree is just a
    /// duplicate.
    /// </remarks>
    public Result AddConsume(ConsumeBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var clash = _consumes.FirstOrDefault(c =>
            string.Equals(c.Queue, binding.Queue, StringComparison.Ordinal) &&
            c.Scope.Overlaps(binding.Scope) &&
            !SameSubjects(c.Subjects, binding.Subjects));

        return clash is not null
            ? Result.Failure(
                ConcordatCodes.BindingConflict,
                $"Queue '{binding.Queue}' is already bound to different subjects in this " +
                "contract.")
            : Add(binding);
    }

    /// <summary>Changes how much this contract may do.</summary>
    /// <param name="enforcement">The new mode.</param>
    public void SetEnforcement(EnforcementMode enforcement) => Enforcement = enforcement;

    /// <summary>Finds the bindings that govern a publish.</summary>
    /// <param name="brokerId">Which broker.</param>
    /// <param name="virtualHost">Which virtual host.</param>
    /// <param name="exchange">The exchange.</param>
    /// <param name="routingKey">The concrete routing key.</param>
    /// <returns>
    /// The matching bindings, highest precedence first. More than one is returned only when
    /// they agree, so a caller may take the first and be right.
    /// </returns>
    public IReadOnlyList<PublishBinding> ResolvePublish(
        Guid brokerId, string virtualHost, string exchange, string routingKey) =>
        [.. _publishes
            .Where(b =>
                b.Scope.Covers(brokerId, virtualHost) &&
                string.Equals(b.Exchange, exchange, StringComparison.Ordinal) &&
                b.RoutingKeyPattern.Matches(routingKey))
            .OrderByDescending(b => b.Precedence ?? 0)];

    /// <summary>Finds the binding that governs a queue.</summary>
    /// <param name="brokerId">Which broker.</param>
    /// <param name="virtualHost">Which virtual host.</param>
    /// <param name="queue">The queue.</param>
    /// <returns>The binding, or null.</returns>
    public ConsumeBinding? ResolveConsume(Guid brokerId, string virtualHost, string queue) =>
        _consumes.FirstOrDefault(b =>
            b.Scope.Covers(brokerId, virtualHost) &&
            string.Equals(b.Queue, queue, StringComparison.Ordinal));

    private Result Add(ConsumeBinding binding)
    {
        _consumes.Add(binding);
        return Result.Success();
    }

    private static bool Conflicts(PublishBinding existing, PublishBinding candidate) =>
        string.Equals(existing.Exchange, candidate.Exchange, StringComparison.Ordinal) &&
        existing.Scope.Overlaps(candidate.Scope) &&
        existing.RoutingKeyPattern.Overlaps(candidate.RoutingKeyPattern) &&
        !SameSubjects(existing.Subjects, candidate.Subjects) &&
        !Separated(existing.Precedence, candidate.Precedence);

    // Distinct precedence is a deliberate ordering. Equal or absent precedence is not.
    private static bool Separated(int? left, int? right) =>
        left is not null && right is not null && left != right;

    private static bool SameSubjects(
        IReadOnlyList<SubjectRef> left, IReadOnlyList<SubjectRef> right) =>
        left.Count == right.Count &&
        left.OrderBy(r => r.Subject.Value, StringComparer.Ordinal)
            .SequenceEqual(right.OrderBy(r => r.Subject.Value, StringComparer.Ordinal));

    /// <summary>
    /// Produces a routing key that matches both patterns, for the error message.
    /// </summary>
    /// <remarks>
    /// A conflict message that names the overlap is actionable; one that only says "these
    /// overlap" leaves the reader to work out why two unlike-looking patterns collide, which is
    /// exactly the part that is hard.
    /// </remarks>
    private static string SampleOverlap(PublishBinding left, PublishBinding right)
    {
        var a = left.RoutingKeyPattern.Value.Split('.');
        var b = right.RoutingKeyPattern.Value.Split('.');
        var sample = new List<string>();

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var word = Word(i < a.Length ? a[i] : "#", i < b.Length ? b[i] : "#");
            if (word is not null)
            {
                sample.Add(word);
            }
        }

        return sample.Count is 0 ? "(the empty key)" : string.Join('.', sample);

        static string? Word(string left, string right)
        {
            if (left is "#" && right is "#")
            {
                return null;
            }

            if (left is not "*" and not "#")
            {
                return left;
            }

            return right is not "*" and not "#" ? right : "x";
        }
    }
}
