using System.Globalization;
using System.Text;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;

namespace Concordat.Formats.Protobuf;

/// <summary>
/// Reduces a proto3 document to canonical text (ADR-015).
/// </summary>
/// <remarks>
/// <para>
/// <b>The canonical form is normalised <c>.proto</c> source, not a serialised
/// <c>FileDescriptorProto</c>.</b> DESIGN §4 names the descriptor, and a descriptor is the
/// right <em>model</em> — it is what the compatibility engine reasons over. But the canonical
/// text is also what the registry <b>stores and serves</b>, and a consumer fetching a Protobuf
/// schema wants source it can hand to <c>protoc</c>. Serving a descriptor blob would repeat
/// exactly the mistake corrected in DECISIONS-PENDING #17: technically sufficient, practically
/// useless.
/// </para>
/// <para>The transformation:</para>
/// <list type="bullet">
///   <item><description>comments dropped; they are the only purely presentational
///     content;</description></item>
///   <item><description><b>type references resolved</b> to leading-dot fullnames wherever the
///     document defines them, so <c>Item</c>, <c>Order.Item</c> and <c>.acme.Order.Item</c>
///     canonicalise identically. A reference reached through an <c>import</c> is left as
///     written — resolving it needs the imported file, which ADR-023 refuses for v1;</description></item>
///   <item><description>imports sorted; DESIGN §12 requires import order not to affect the
///     schema id;</description></item>
///   <item><description>definitions sorted by name, fields by <b>number</b>, enum values by
///     number, options by name — declaration order carries no meaning in Protobuf, and the
///     number is the field's identity;</description></item>
///   <item><description>reserved ranges merged and sorted, so <c>reserved 3, 2;</c> and
///     <c>reserved 2 to 3;</c> are one schema.</description></item>
/// </list>
/// <para>
/// <b>The output is indented rather than minified</b>, unlike the JSON and Avro forms. Those
/// are consumed by machines; this one is compiled by people. Determinism and idempotence are
/// what canonicalisation requires, and neither needs the whitespace removed.
/// </para>
/// </remarks>
public sealed class ProtoSchemaCanonicalizer : ISchemaCanonicalizer
{
    private const string Indent = "  ";

    /// <inheritdoc />
    public SchemaFormat Format => SchemaFormat.Protobuf;

    /// <inheritdoc />
    public Result<string> Canonicalize(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Result<string>.Failure(
                ConcordatCodes.SchemaBodyEmpty, "A schema body is required.");
        }

        // Checked before parsing, not only by Schema.Create afterwards: that check runs on the
        // canonical output, so an oversized raw document is otherwise fully lexed, parsed and
        // walked before anything refuses it.
        var bytes = Encoding.UTF8.GetByteCount(body);
        if (bytes > Schema.MaxBodyBytes)
        {
            return Result<string>.Failure(
                ConcordatCodes.SchemaTooLarge,
                $"A schema body may be at most {Schema.MaxBodyBytes} bytes; got {bytes}.");
        }

        ProtoFile file;
        try
        {
            file = ProtoParser.Parse(body);
        }
        catch (MalformedProtoSchemaException ex)
        {
            return Result<string>.Failure(ConcordatCodes.SchemaMalformed, ex.Message);
        }

        return Result<string>.Success(Write(file));
    }

    internal static string Write(ProtoFile file)
    {
        var output = new StringBuilder();
        output.Append("syntax = \"proto3\";\n");

        if (file.Package is not null)
        {
            output.Append(CultureInfo.InvariantCulture, $"package {file.Package};\n");
        }

        foreach (var import in file.Imports.Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal))
        {
            output.Append(CultureInfo.InvariantCulture, $"import \"{import}\";\n");
        }

        WriteOptions(output, file.Options, "");

        foreach (var @enum in file.Enums.OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            WriteEnum(output, @enum, "");
        }

        foreach (var message in file.Messages.OrderBy(m => m.FullName, StringComparer.Ordinal))
        {
            WriteMessage(output, message, "");
        }

        return output.ToString();
    }

    private static void WriteMessage(StringBuilder output, ProtoMessage message, string indent)
    {
        output.Append(CultureInfo.InvariantCulture, $"{indent}message {LocalName(message.FullName)} {{\n");
        var inner = indent + Indent;

        WriteReserved(output, message.Reserved, inner);
        WriteOptions(output, message.Options, inner);

        foreach (var field in message.Fields.OrderBy(f => f.Number))
        {
            WriteField(output, field, inner);
        }

        foreach (var oneof in message.Oneofs.OrderBy(o => o.Name, StringComparer.Ordinal))
        {
            output.Append(CultureInfo.InvariantCulture, $"{inner}oneof {oneof.Name} {{\n");
            foreach (var field in oneof.Fields.OrderBy(f => f.Number))
            {
                WriteField(output, field, inner + Indent);
            }

            output.Append(CultureInfo.InvariantCulture, $"{inner}}}\n");
        }

        foreach (var @enum in message.Enums.OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            WriteEnum(output, @enum, inner);
        }

        foreach (var nested in message.Messages.OrderBy(m => m.FullName, StringComparer.Ordinal))
        {
            WriteMessage(output, nested, inner);
        }

        output.Append(CultureInfo.InvariantCulture, $"{indent}}}\n");
    }

    private static void WriteField(StringBuilder output, ProtoField field, string indent)
    {
        var label = field.Label switch
        {
            ProtoLabel.Repeated => "repeated ",
            ProtoLabel.Optional => "optional ",
            _ => "",
        };

        var type = field.IsMap
            ? $"map<{field.MapKeyType}, {field.TypeName}>"
            : field.TypeName;

        output.Append(
            CultureInfo.InvariantCulture,
            $"{indent}{label}{type} {field.Name} = " +
            $"{field.Number.ToString(CultureInfo.InvariantCulture)}{InlineOptions(field.Options)};\n");
    }

    private static void WriteEnum(StringBuilder output, ProtoEnum @enum, string indent)
    {
        output.Append(CultureInfo.InvariantCulture, $"{indent}enum {LocalName(@enum.FullName)} {{\n");
        var inner = indent + Indent;

        WriteReserved(output, @enum.Reserved, inner);
        WriteOptions(output, @enum.Options, inner);

        foreach (var value in @enum.Values.OrderBy(v => v.Number))
        {
            output.Append(
                CultureInfo.InvariantCulture,
                $"{inner}{value.Name} = " +
                $"{value.Number.ToString(CultureInfo.InvariantCulture)}{InlineOptions(value.Options)};\n");
        }

        output.Append(CultureInfo.InvariantCulture, $"{indent}}}\n");
    }

    private static void WriteReserved(StringBuilder output, ProtoReserved reserved, string indent)
    {
        foreach (var range in Merge(reserved.Ranges))
        {
            var start = range.Start.ToString(CultureInfo.InvariantCulture);
            var span = range.Start == range.End
                ? start
                : $"{start} to {range.End.ToString(CultureInfo.InvariantCulture)}";

            output.Append(CultureInfo.InvariantCulture, $"{indent}reserved {span};\n");
        }

        foreach (var name in reserved.Names.Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal))
        {
            output.Append(CultureInfo.InvariantCulture, $"{indent}reserved \"{name}\";\n");
        }
    }

    /// <summary>Sorts reserved ranges and coalesces the ones that touch or overlap.</summary>
    /// <remarks>
    /// Without this, <c>reserved 2, 3;</c> and <c>reserved 2 to 3;</c> reserve the same numbers
    /// and produce different schema ids — precisely the near-duplicate accumulation
    /// canonicalisation exists to prevent.
    /// </remarks>
    internal static List<ProtoReservedRange> Merge(IReadOnlyList<ProtoReservedRange> ranges)
    {
        var merged = new List<ProtoReservedRange>();

        foreach (var range in ranges.OrderBy(r => r.Start).ThenBy(r => r.End))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End + 1)
            {
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, range.End) };
                continue;
            }

            merged.Add(range);
        }

        return merged;
    }

    private static void WriteOptions(
        StringBuilder output, IReadOnlyList<ProtoOption> options, string indent)
    {
        foreach (var option in options.OrderBy(o => o.Name, StringComparer.Ordinal))
        {
            output.Append(CultureInfo.InvariantCulture, $"{indent}option {option.Name} = {option.Value};\n");
        }
    }

    private static string InlineOptions(IReadOnlyList<ProtoOption> options)
    {
        if (options.Count == 0)
        {
            return "";
        }

        var rendered = options
            .OrderBy(o => o.Name, StringComparer.Ordinal)
            .Select(o => $"{o.Name} = {o.Value}");

        return $" [{string.Join(", ", rendered)}]";
    }

    private static string LocalName(string fullName)
    {
        var lastDot = fullName.LastIndexOf('.');
        return lastDot < 0 ? fullName : fullName[(lastDot + 1)..];
    }
}
