namespace Concordat.Domain.Registry;

/// <summary>
/// The AMQP header names carrying schema identity (ADR-010).
/// </summary>
/// <remarks>
/// <para>
/// Normative protocol under ADR-019. Until now these names existed only in prose in DESIGN §2,
/// which means a rename there, or a typo in one of five SDKs, would have been invisible to
/// every build and would only have surfaced cross-language — the exact silent-mismatch class
/// the conformance corpus exists to catch.
/// </para>
/// <para>
/// <b>No <c>x-</c> prefix.</b> RabbitMQ converts <c>x-</c>-prefixed 0-9-1 headers into AMQP 1.0
/// message-annotations while all others become application-properties, which is where
/// application metadata belongs. That single constraint is what makes ADR-013's "designed to
/// survive 1.0 conversion" claim mean anything. <c>x-</c> is also reserved by RabbitMQ itself:
/// <c>x-death</c>, <c>x-delay</c>, <c>x-delivery-count</c>, <c>x-stream-filter-value</c>.
/// </para>
/// <para>
/// <b>Every value is a UTF-8 string.</b> Not a convenience: NServiceBus and Rebus expose
/// headers as <c>Dictionary&lt;string,string&gt;</c> and physically cannot carry an integer.
/// It is also the lowest common denominator every AMQP client in every language can write,
/// with no numeric coercion to specify twice.
/// </para>
/// </remarks>
public static class EnvelopeHeaders
{
    /// <summary>
    /// The envelope version. Currently the literal <c>"1"</c> — not <c>"v1"</c>, not <c>"1.0"</c>.
    /// </summary>
    /// <remarks>
    /// Required. Its presence is what identifies a message as carrying a Mode A envelope, and
    /// it is what lets the envelope evolve at all. Azure's unversioned <c>avro/binary+{id}</c>
    /// scheme is the documented dead end this exists to avoid.
    /// </remarks>
    public const string Version = "concordat-v";

    /// <summary>The content-addressed schema id: 32 lowercase hexadecimal characters.</summary>
    /// <remarks>Required whenever <see cref="Version"/> is present.</remarks>
    public const string SchemaId = "concordat-schema-id";

    /// <summary>The subject name, in the canonical dot-separated grammar.</summary>
    /// <remarks>
    /// Optional. When absent a reader falls back to <c>properties.type</c>, and failing that can
    /// recover it from <c>GET /v1/schemas/{id}/subjects</c>.
    /// </remarks>
    public const string Subject = "concordat-subject";

    /// <summary>The version ordinal, in invariant base-10.</summary>
    /// <remarks>
    /// Optional and advisory: the schema id already pins the exact schema, so a malformed
    /// ordinal is a warning rather than grounds for rejecting a structurally valid message.
    /// </remarks>
    public const string VersionOrdinal = "concordat-version";

    /// <summary>The optional semantic version label.</summary>
    /// <remarks>Advisory only. A human label must never quarantine a valid payload.</remarks>
    public const string Semver = "concordat-semver";

    /// <summary>The schema language: one of the <see cref="WireTokens"/> format tokens.</summary>
    public const string Format = "concordat-format";

    /// <summary>The current envelope version value.</summary>
    public const string CurrentVersion = "1";

    /// <summary>Every header name this version of the envelope defines.</summary>
    /// <remarks>
    /// Exposed so a test can assert the set has not silently grown or been renamed, and so a
    /// writer can strip a stale envelope before applying a fresh one.
    /// </remarks>
    public static IReadOnlyList<string> All { get; } =
    [
        Version,
        SchemaId,
        Subject,
        VersionOrdinal,
        Semver,
        Format,
    ];

    /// <summary>
    /// Prefixes owned by other messaging libraries, which Concordat must never write into.
    /// </summary>
    /// <remarks>
    /// Checked by a test rather than trusted. A collision would be silent and would corrupt
    /// another library's routing rather than merely failing validation.
    /// </remarks>
    public static IReadOnlyList<string> ForeignPrefixes { get; } =
    [
        "x-",
        "MT-",
        "NServiceBus.",
        "rbs2-",
        "rabbitmq-",
        "ce-",
        "cloudEvents",
    ];
}
