using Concordat.Domain.Messaging;

namespace Concordat.RabbitMq;

/// <summary>Configuration for the RabbitMQ middleware.</summary>
public sealed class ConcordatRabbitMqOptions
{
    /// <summary>How much Concordat may do about what it finds.</summary>
    /// <remarks>
    /// <see cref="EnforcementMode.Monitor"/> by default. Defaulting to
    /// <see cref="EnforcementMode.Enforce"/> would mean that adding a package reference could
    /// start rejecting production traffic, which is not a decision a dependency gets to make.
    /// </remarks>
    public EnforcementMode Mode { get; set; } = EnforcementMode.Monitor;

    /// <summary>Whether payload validation runs at all.</summary>
    /// <remarks>
    /// On by default, per M2.4. Turning it off leaves the envelope — identity without
    /// verification — which is a reasonable position for a very hot path where the schema is
    /// enforced at the edge instead.
    /// </remarks>
    public bool ValidatePayloads { get; set; } = true;

    /// <summary>The exchange non-conforming deliveries are routed to.</summary>
    public string QuarantineExchange { get; set; } = "concordat.quarantine";

    /// <summary>
    /// Whether the middleware declares the quarantine exchange itself.
    /// </summary>
    /// <remarks>
    /// On by default, because the alternative is that the first quarantine in production fails
    /// on a missing exchange — the worst possible moment to discover a topology gap. Turn it
    /// off where topology is owned by infrastructure-as-code and applications lack the rights.
    /// </remarks>
    public bool DeclareQuarantineExchange { get; set; } = true;

    /// <summary>How the subject is derived on the publish side.</summary>
    public ISubjectResolver SubjectResolver { get; set; } = MessageTypeSubjectResolver.Instance;

    /// <summary>Where enforcement decisions go.</summary>
    public IEnforcementObserver Observer { get; set; } = new EnforcementCounters();

    /// <summary>Validates the options, throwing on anything that cannot work.</summary>
    /// <exception cref="InvalidOperationException">A required value is missing.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(QuarantineExchange))
        {
            throw new InvalidOperationException($"{nameof(QuarantineExchange)} is required.");
        }

        ArgumentNullException.ThrowIfNull(SubjectResolver);
        ArgumentNullException.ThrowIfNull(Observer);
    }
}
