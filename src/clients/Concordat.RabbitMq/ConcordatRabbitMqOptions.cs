using Concordat.Domain.Messaging;
using Concordat.Domain.Registry;

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
    /// <para>
    /// <b>On by default, and that makes <c>configure</c> on <c>concordat.quarantine</c> a
    /// requirement rather than a preference</b> (decision 14). See
    /// <see href="https://github.com/RafaelJCamara/Concordat/blob/main/docs/BROKER-PERMISSIONS.md">
    /// BROKER-PERMISSIONS.md</see>.
    /// </para>
    /// <para>
    /// The alternative is assuming the exchange exists, and the first quarantine in production
    /// then fails on a missing one. Consider when that happens: quarantine only runs once a
    /// message has already violated its contract under <c>ENFORCE</c>, so the exchange is needed
    /// for the first time during an incident, in a path nobody has exercised — a second failure
    /// stacked on the one being handled. Declaring costs one idempotent <c>exchange.declare</c>
    /// per process.
    /// </para>
    /// <para>
    /// <b>Turning this off requires provisioning the exchange yourself</b>, as a durable fanout.
    /// Turning it off without doing so looks fine until the first violation, which is the exact
    /// failure the default avoids.
    /// </para>
    /// </remarks>
    public bool DeclareQuarantineExchange { get; set; } = true;

    /// <summary>
    /// Whether the environment's contracts govern routes, or only <see cref="Mode"/> does (M7.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// On by default. A deployment with no contracts pays one resolve per route, cached
    /// thereafter, and gets <c>GovernedRoutes = 0</c> in the client status — which is worth
    /// having, because "no contract covers anything we send" otherwise looks exactly like
    /// working governance.
    /// </para>
    /// <para>
    /// Turning it off pins every route to <see cref="Mode"/> and is for a service that must not
    /// have its enforcement changed from outside, whatever an operator does in the registry.
    /// </para>
    /// </remarks>
    public bool ConsultContracts { get; set; } = true;

    /// <summary>How the subject is derived on the publish side.</summary>
    /// <remarks>
    /// Consulted first. When it yields nothing and a contract governs the route with exactly one
    /// permitted subject, that subject is used instead — which is how an un-instrumented
    /// publisher becomes enforceable without touching its code.
    /// </remarks>
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
