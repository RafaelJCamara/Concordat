namespace Concordat.Formats.Protobuf;

/// <summary>Unwinds the parse of a malformed <c>.proto</c> document.</summary>
/// <remarks>
/// Never crosses the public API: every entry point converts it to a <c>schema_malformed</c>
/// failure, because the format ports report bad input as a <c>Result</c> rather than a throw.
/// </remarks>
/// <param name="message">What is wrong, and where.</param>
internal sealed class MalformedProtoSchemaException(string message) : Exception(message);

/// <summary>A field's cardinality.</summary>
internal enum ProtoLabel
{
    /// <summary>The proto3 default: a singular field.</summary>
    Singular = 1,

    /// <summary>A repeated field, which changes the encoding, not just the type.</summary>
    Repeated = 2,

    /// <summary>
    /// An explicitly optional proto3 field. Encoding is identical to
    /// <see cref="Singular"/>; it only adds presence tracking, so it never affects the wire.
    /// </summary>
    Optional = 3,
}

/// <summary>An option, kept as text.</summary>
/// <param name="Name">The option name, including any parenthesised extension name.</param>
/// <param name="Value">The value exactly as written.</param>
/// <remarks>
/// Values are not interpreted. The compatibility engine reads only <c>json_name</c>, and
/// keeping the rest as opaque text means an option this parser has never heard of survives
/// canonicalisation instead of being silently dropped — the mistake corrected in
/// DECISIONS-PENDING #17.
/// </remarks>
internal sealed record ProtoOption(string Name, string Value);

/// <summary>A reserved number range, inclusive at both ends.</summary>
/// <param name="Start">The first reserved number.</param>
/// <param name="End">The last reserved number; equal to <paramref name="Start"/> for a single.</param>
internal sealed record ProtoReservedRange(int Start, int End);

/// <summary>What a message or enum has reserved against reuse.</summary>
/// <param name="Ranges">Reserved number ranges.</param>
/// <param name="Names">Reserved field names.</param>
internal sealed record ProtoReserved(
    IReadOnlyList<ProtoReservedRange> Ranges,
    IReadOnlyList<string> Names)
{
    /// <summary>Nothing reserved.</summary>
    public static ProtoReserved Empty { get; } = new([], []);

    /// <summary>Whether a field number is reserved here.</summary>
    /// <param name="number">The field number.</param>
    /// <returns><see langword="true"/> when some range covers it.</returns>
    public bool Covers(int number) => Ranges.Any(r => number >= r.Start && number <= r.End);
}

/// <summary>A message field.</summary>
/// <param name="Name">The field name. Not transmitted on the wire — see the compatibility engine.</param>
/// <param name="Number">The field number. This <em>is</em> the field's identity on the wire.</param>
/// <param name="TypeName">
/// The scalar keyword, or a type reference resolved to a leading-dot fullname where this
/// document defines it.
/// </param>
/// <param name="Label">Cardinality.</param>
/// <param name="MapKeyType">The key type when this field is a <c>map</c>, otherwise null.</param>
/// <param name="Options">Field options, in declaration order.</param>
/// <param name="OneofName">The oneof this field belongs to, if any.</param>
internal sealed record ProtoField(
    string Name,
    int Number,
    string TypeName,
    ProtoLabel Label,
    string? MapKeyType,
    IReadOnlyList<ProtoOption> Options,
    string? OneofName)
{
    /// <summary>Whether this field is a map, which encodes as a repeated entry message.</summary>
    public bool IsMap => MapKeyType is not null;
}

/// <summary>A oneof group.</summary>
/// <param name="Name">The group name.</param>
/// <param name="Fields">Its member fields.</param>
internal sealed record ProtoOneof(string Name, IReadOnlyList<ProtoField> Fields);

/// <summary>An enum value.</summary>
/// <param name="Name">The value name, which is what proto3 JSON emits.</param>
/// <param name="Number">The value number, which is what the wire carries.</param>
/// <param name="Options">Value options.</param>
internal sealed record ProtoEnumValue(string Name, int Number, IReadOnlyList<ProtoOption> Options);

/// <summary>An enum definition.</summary>
/// <param name="FullName">The resolved fullname, with a leading dot.</param>
/// <param name="Values">Values in declaration order.</param>
/// <param name="Reserved">Numbers and names reserved against reuse.</param>
/// <param name="Options">Enum options.</param>
internal sealed record ProtoEnum(
    string FullName,
    IReadOnlyList<ProtoEnumValue> Values,
    ProtoReserved Reserved,
    IReadOnlyList<ProtoOption> Options);

/// <summary>A message definition.</summary>
/// <param name="FullName">The resolved fullname, with a leading dot.</param>
/// <param name="Fields">Fields declared directly, excluding oneof members.</param>
/// <param name="Oneofs">Oneof groups.</param>
/// <param name="Enums">Nested enums.</param>
/// <param name="Messages">Nested messages.</param>
/// <param name="Reserved">Numbers and names reserved against reuse.</param>
/// <param name="Options">Message options.</param>
internal sealed record ProtoMessage(
    string FullName,
    IReadOnlyList<ProtoField> Fields,
    IReadOnlyList<ProtoOneof> Oneofs,
    IReadOnlyList<ProtoEnum> Enums,
    IReadOnlyList<ProtoMessage> Messages,
    ProtoReserved Reserved,
    IReadOnlyList<ProtoOption> Options)
{
    /// <summary>Every field including oneof members, which is what the wire sees.</summary>
    /// <remarks>
    /// Oneof membership changes nothing about how a field is encoded — the group is a
    /// source-level construct that guarantees at most one member is set. Compatibility is
    /// therefore judged over the flattened set.
    /// </remarks>
    public IEnumerable<ProtoField> AllFields =>
        Fields.Concat(Oneofs.SelectMany(o => o.Fields));
}

/// <summary>A parsed <c>.proto</c> file.</summary>
/// <param name="Package">The package, or null when none was declared.</param>
/// <param name="Imports">Imported filenames, as written.</param>
/// <param name="Messages">Top-level messages.</param>
/// <param name="Enums">Top-level enums.</param>
/// <param name="Options">File-level options.</param>
internal sealed record ProtoFile(
    string? Package,
    IReadOnlyList<string> Imports,
    IReadOnlyList<ProtoMessage> Messages,
    IReadOnlyList<ProtoEnum> Enums,
    IReadOnlyList<ProtoOption> Options)
{
    /// <summary>Every message in the file, nested ones included, keyed by fullname.</summary>
    public IReadOnlyDictionary<string, ProtoMessage> AllMessages()
    {
        var map = new Dictionary<string, ProtoMessage>(StringComparer.Ordinal);
        foreach (var message in Messages)
        {
            Collect(message, map);
        }

        return map;
    }

    /// <summary>Every enum in the file, nested ones included, keyed by fullname.</summary>
    public IReadOnlyDictionary<string, ProtoEnum> AllEnums()
    {
        var map = new Dictionary<string, ProtoEnum>(StringComparer.Ordinal);
        foreach (var @enum in Enums)
        {
            map[@enum.FullName] = @enum;
        }

        foreach (var message in Messages)
        {
            CollectEnums(message, map);
        }

        return map;
    }

    private static void Collect(ProtoMessage message, Dictionary<string, ProtoMessage> map)
    {
        map[message.FullName] = message;
        foreach (var nested in message.Messages)
        {
            Collect(nested, map);
        }
    }

    private static void CollectEnums(ProtoMessage message, Dictionary<string, ProtoEnum> map)
    {
        foreach (var @enum in message.Enums)
        {
            map[@enum.FullName] = @enum;
        }

        foreach (var nested in message.Messages)
        {
            CollectEnums(nested, map);
        }
    }
}
