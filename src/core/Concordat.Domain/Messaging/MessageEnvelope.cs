using Concordat.Domain.Registry;

namespace Concordat.Domain.Messaging;

/// <summary>Something questionable about an envelope that must not reject the message.</summary>
/// <param name="Code">A constant from <see cref="Results.ConcordatCodes"/>.</param>
/// <param name="Message">What was wrong.</param>
/// <remarks>
/// The warn/reject split is the load-bearing judgement in envelope reading. The schema id
/// pins the exact schema, so a mistyped version ordinal or semver label tells us nothing we
/// did not already know — quarantining a structurally valid payload over a human label would
/// be a self-inflicted outage.
/// </remarks>
public sealed record EnvelopeWarning(string Code, string Message);

/// <summary>Schema identity read off a message.</summary>
/// <param name="SchemaId">The content-addressed schema id. Always present.</param>
/// <param name="Subject">The subject, when the producer supplied one.</param>
/// <param name="Ordinal">The version ordinal, advisory.</param>
/// <param name="Semver">The semantic version label, advisory.</param>
/// <param name="Format">The declared schema language.</param>
/// <param name="Warnings">Advisory problems that did not prevent reading.</param>
public sealed record MessageEnvelope(
    SchemaId SchemaId,
    SubjectName? Subject,
    int? Ordinal,
    SemanticVersion? Semver,
    SchemaFormat? Format,
    IReadOnlyList<EnvelopeWarning> Warnings);

/// <summary>How a message carries its schema identity.</summary>
public enum EnvelopeKind
{
    /// <summary>No Concordat identity at all. Not an error — this is the brownfield path.</summary>
    None = 1,

    /// <summary>Mode A: identity in AMQP headers, payload untouched.</summary>
    Headers = 2,

    /// <summary>Mode B: identity in the content-type token, payload untouched.</summary>
    ContentType = 3,
}
