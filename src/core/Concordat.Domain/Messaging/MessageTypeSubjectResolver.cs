using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Domain.Messaging;

/// <summary>
/// The default strategy: the subject is the message type (ADR-011).
/// </summary>
/// <remarks>
/// <para>
/// Reads <c>properties.type</c> as-is — "as-is" meaning the subject is not <em>derived</em>
/// from the exchange or routing key, which are high-cardinality, dynamic, and unknown to the
/// consumer. What the publisher declared is then put through
/// <see cref="SubjectNormalizer"/> and validated, because a .NET publisher writing
/// <c>typeof(T).AssemblyQualifiedName</c> would otherwise never match the registered subject.
/// </para>
/// <para>
/// The stable contract in RabbitMQ is <em>what a message is</em>, not where it went, and the
/// type is the only identifier a publisher and a consumer both possess.
/// </para>
/// </remarks>
public sealed class MessageTypeSubjectResolver : ISubjectResolver
{
    /// <summary>A shared instance. The resolver holds no state.</summary>
    public static MessageTypeSubjectResolver Instance { get; } = new();

    /// <inheritdoc />
    public SubjectResolution Resolve(PublishContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Absent, not invalid. properties.type is optional in AMQP and an un-instrumented
        // publisher is the ordinary brownfield state, not a bug to report on every message.
        if (string.IsNullOrWhiteSpace(context.MessageType))
        {
            return SubjectResolution.NoSubject;
        }

        var normalized = SubjectNormalizer.Normalize(context.MessageType);
        var subject = SubjectName.Create(normalized);

        if (subject.IsSuccess)
        {
            return SubjectResolution.Resolved(subject.Value);
        }

        // A generic type name is the one invalid case a .NET publisher hits by accident, so it
        // gets an answer rather than a grammar complaint. It is REFUSED rather than mangled:
        // inventing a spelling for `List`1[[Acme.Order]]` would commit every other SDK to
        // reproducing that invention exactly, which is the cross-language split ADR-019 exists
        // to prevent. Publish a named type instead.
        if (normalized.Contains('`', StringComparison.Ordinal)
            || normalized.Contains('[', StringComparison.Ordinal))
        {
            return SubjectResolution.Unusable(
                ConcordatCodes.SubjectNameInvalid,
                $"'{context.MessageType}' looks like a generic type. Concordat does not spell " +
                "generic type names, because every SDK would have to spell them identically. " +
                "Publish a named type, or set properties.type explicitly.");
        }

        return SubjectResolution.Unusable(
            ConcordatCodes.SubjectNameInvalid,
            $"'{context.MessageType}' cannot be used as a subject: {subject.Error!.Message}");
    }
}
