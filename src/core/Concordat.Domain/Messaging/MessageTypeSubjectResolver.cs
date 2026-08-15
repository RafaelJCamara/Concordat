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
/// <para>
/// <b>A closed generic resolves</b> to the normative spelling <c>Outer_of_Arg</c> (ADR-025) —
/// so a publisher sending <c>Envelope&lt;OrderCreated&gt;</c> gets a subject rather than a
/// refusal, and a Go or Python consumer of the same logical contract derives the same string
/// from its own generic type.
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

        // A generic name that still carries CLR syntax after normalisation is one the spelling
        // could not parse -- an open generic, or a malformed name. It gets an answer rather than
        // a grammar complaint, because it is the one invalid case a .NET publisher hits by
        // accident. A CLOSED generic is spelled rather than refused (ADR-025): the spelling is
        // defined over the outer and argument names in order, which every language with generics
        // can produce, rather than over CLR syntax, which only .NET can.
        if (normalized.Contains('`', StringComparison.Ordinal)
            || normalized.Contains('[', StringComparison.Ordinal))
        {
            return SubjectResolution.Unusable(
                ConcordatCodes.SubjectNameInvalid,
                $"'{context.MessageType}' looks like a generic type this build cannot spell — " +
                "an open generic, or a name it could not parse. A closed generic such as " +
                "Envelope<OrderCreated> is spelled 'Envelope_of_OrderCreated'. Publish a closed " +
                "type, or set properties.type explicitly.");
        }

        return SubjectResolution.Unusable(
            ConcordatCodes.SubjectNameInvalid,
            $"'{context.MessageType}' cannot be used as a subject: {subject.Error!.Message}");
    }
}
