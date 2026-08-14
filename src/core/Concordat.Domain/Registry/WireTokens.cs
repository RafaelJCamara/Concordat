namespace Concordat.Domain.Registry;

/// <summary>
/// The stable wire spellings of domain enumerations.
/// </summary>
/// <remarks>
/// These strings are normative protocol (ADR-019). They appear in the
/// <c>concordat-format</c> envelope header, in REST payloads, and inside the schema-id
/// preimage, so they are deliberately not <c>Enum.ToString()</c> — renaming a C# member must
/// not silently change the wire format or invalidate every stored schema id.
/// </remarks>
public static class WireTokens
{
    /// <summary>The wire token for <see cref="SchemaFormat.Json"/>.</summary>
    public const string FormatJson = "json";

    /// <summary>The wire token for <see cref="SchemaFormat.Avro"/>.</summary>
    public const string FormatAvro = "avro";

    /// <summary>The wire token for <see cref="SchemaFormat.Protobuf"/>.</summary>
    public const string FormatProtobuf = "protobuf";

    /// <summary>Maps a format to its wire token.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The stable token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format is not a known member.</exception>
    public static string For(SchemaFormat format) => format switch
    {
        SchemaFormat.Json => FormatJson,
        SchemaFormat.Avro => FormatAvro,
        SchemaFormat.Protobuf => FormatProtobuf,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown schema format."),
    };

    // ------------------------------------------------------------------------ M6.1
    //
    // Added when the protocol freeze found the API serialising these from CLR member names
    // with ToUpperInvariant(). That produced WIREJSON from one endpoint and WIRE_JSON from
    // another for the same value, and made every C# rename a silent wire-format change --
    // the exact failure the tokens above already existed to prevent.

    /// <summary>Maps a compatibility mode to its wire token.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns>The stable token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The mode is not a known member.</exception>
    public static string For(CompatibilityMode mode) => mode switch
    {
        CompatibilityMode.None => "NONE",
        CompatibilityMode.Backward => "BACKWARD",
        CompatibilityMode.BackwardTransitive => "BACKWARD_TRANSITIVE",
        CompatibilityMode.Forward => "FORWARD",
        CompatibilityMode.ForwardTransitive => "FORWARD_TRANSITIVE",
        CompatibilityMode.Full => "FULL",
        CompatibilityMode.FullTransitive => "FULL_TRANSITIVE",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown compatibility mode."),
    };

    /// <summary>Maps a compatibility surface to its wire token.</summary>
    /// <param name="surface">The surface.</param>
    /// <returns>The stable token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The surface is not a known member.</exception>
    public static string For(CompatibilitySurface surface) => surface switch
    {
        CompatibilitySurface.Wire => "WIRE",
        CompatibilitySurface.WireJson => "WIRE_JSON",
        CompatibilitySurface.Source => "SOURCE",
        _ => throw new ArgumentOutOfRangeException(
            nameof(surface), surface, "Unknown compatibility surface."),
    };

    /// <summary>Maps a subject lifecycle to its wire token.</summary>
    /// <param name="lifecycle">The lifecycle.</param>
    /// <returns>The stable token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The lifecycle is not a known member.</exception>
    public static string For(SubjectLifecycle lifecycle) => lifecycle switch
    {
        SubjectLifecycle.Active => "ACTIVE",
        SubjectLifecycle.Deprecated => "DEPRECATED",
        SubjectLifecycle.Retired => "RETIRED",
        _ => throw new ArgumentOutOfRangeException(
            nameof(lifecycle), lifecycle, "Unknown subject lifecycle."),
    };

    /// <summary>Maps a content model to its wire token.</summary>
    /// <param name="contentModel">The content model.</param>
    /// <returns>The stable token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a known member.</exception>
    public static string For(ContentModel contentModel) => contentModel switch
    {
        ContentModel.Open => "OPEN",
        ContentModel.Closed => "CLOSED",
        _ => throw new ArgumentOutOfRangeException(
            nameof(contentModel), contentModel, "Unknown content model."),
    };

    /// <summary>Maps a version status to its wire token.</summary>
    /// <param name="status">The status.</param>
    /// <returns>The stable token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The status is not a known member.</exception>
    public static string For(VersionStatus status) => status switch
    {
        VersionStatus.Active => "ACTIVE",
        VersionStatus.AwaitingApproval => "AWAITING_APPROVAL",
        VersionStatus.Rejected => "REJECTED",
        VersionStatus.Dismissed => "DISMISSED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown version status."),
    };

    /// <summary>Maps an enforcement mode to its wire token.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns>The stable token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The mode is not a known member.</exception>
    public static string For(EnforcementMode mode) => mode switch
    {
        EnforcementMode.Off => "OFF",
        EnforcementMode.Monitor => "MONITOR",
        EnforcementMode.Enforce => "ENFORCE",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown enforcement mode."),
    };
}
