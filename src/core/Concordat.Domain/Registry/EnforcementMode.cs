namespace Concordat.Domain.Registry;

/// <summary>How much Concordat is allowed to do about what it finds.</summary>
/// <remarks>
/// The three-step adoption path, and the reason it has three steps rather than a boolean: no
/// team turns on enforcement across an existing estate in one change. <see cref="Monitor"/> is
/// where the argument gets won, by showing which publishers are already violating a contract
/// nobody had written down.
/// </remarks>
public enum EnforcementMode
{
    /// <summary>Do nothing. No envelope is written, no payload is read.</summary>
    /// <remarks>
    /// A real setting, not a debug one: it is the switch an operator reaches for at 3am, and it
    /// must cost nothing and touch nothing.
    /// </remarks>
    Off = 1,

    /// <summary>
    /// Stamp the envelope and report violations, but never block or quarantine anything.
    /// </summary>
    /// <remarks>
    /// The default for adoption. Publishers still get an envelope — consumers downstream can
    /// start reading identity immediately — while a violation only ever produces a report.
    /// </remarks>
    Monitor = 2,

    /// <summary>Block invalid publishes and quarantine invalid deliveries.</summary>
    Enforce = 3,
}

// Moved here from Concordat.RabbitMq in M7.3. It was client vocabulary until a Contract needed
// to carry the same setting, and two enums spelling one concept is how a middleware in Monitor
// and a contract in Enforce come to disagree about what the estate is doing.
