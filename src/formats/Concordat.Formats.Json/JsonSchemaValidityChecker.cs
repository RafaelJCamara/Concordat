using System.Globalization;
using System.Text.Json;
using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;

namespace Concordat.Formats.Json;

/// <summary>
/// Reports where a JSON document is not a valid draft 2020-12 schema (ADR-039).
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of defect, chosen because each is something no validator can accept and neither
/// needs a judgement call:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>The meta-schema's assertions.</b> A <c>type</c> that names no type, a <c>minimum</c>
///     that is a string, a <c>required</c> that is <c>true</c>, a <c>properties</c> whose member
///     is not a schema. Read from the specification's own files by
///     <see cref="MetaschemaValidator"/>, so "valid" means exactly what the specification says.
///   </description></item>
///   <item><description>
///     <b>A <c>$ref</c> into this document that points at nothing.</b> <c>#/$defs/Adress</c>
///     where the document says <c>Address</c>. The meta-schema does not look at this (it checks
///     the shape of a reference, not whether it leads anywhere), and it is the commonest way a
///     schema that passes the first check still cannot be compiled.
///   </description></item>
/// </list>
/// <para>
/// <b>What it deliberately does not do</b>, because each would refuse a schema somebody was
/// entitled to register: it does not assert <c>format</c> (so a <c>pattern</c> that is not a
/// regular expression is not a defect; ADR-039 records this as a gap and not an oversight), it
/// does not refuse an unknown keyword or vocabulary, it does not follow a reference to another
/// document, and it does not say where a <c>$ref</c> points once any resource inside the
/// document declares its own <c>$id</c>.
/// </para>
/// </remarks>
public sealed class JsonSchemaValidityChecker : ISchemaValidityChecker
{
    /// <inheritdoc />
    public SchemaFormat Format => SchemaFormat.Json;

    /// <inheritdoc />
    public IReadOnlyList<SchemaDefect> Check(string canonicalBody)
    {
        if (string.IsNullOrWhiteSpace(canonicalBody))
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(canonicalBody);
        }
        catch (JsonException)
        {
            // Malformed input is the canonicaliser's to report, and it already has.
            return [];
        }

        using (document)
        {
            var found = new List<SchemaDefect>();

            foreach (var (path, message) in Draft202012Metaschema.Validator.Validate(document.RootElement))
            {
                found.Add(new SchemaDefect(path, SchemaDefectKinds.MetaschemaViolation, message));
            }

            // A document that is not a schema has no schema positions to look for references in,
            // and reading one as if it had would report a $ref the author put in a list of numbers.
            if (found.Count == 0)
            {
                FindUnresolvedReferences(document.RootElement, found);
            }

            return [.. found
                .OrderBy(d => d.Path, StringComparer.Ordinal)
                .ThenBy(d => d.Kind, StringComparer.Ordinal)];
        }
    }

    // ---------------------------------------------------------------- references

    private static void FindUnresolvedReferences(JsonElement root, List<SchemaDefect> found)
    {
        if (root.ValueKind is not JsonValueKind.Object || DeclaresInnerResource(root, isRoot: true))
        {
            // With another resource inside the document, '#/...' in its subschemas is relative to
            // that resource, and saying where it points means resolving the whole identifier
            // graph. Refusing a schema on a guess about that is worse than not looking.
            return;
        }

        WalkSchema(root, root, "#", found);
    }

    /// <summary>Whether any object below the root has a string-valued <c>$id</c>.</summary>
    /// <remarks>
    /// Looks everywhere, including inside data (<c>default</c>, <c>const</c>), which can only make
    /// it skip a check that would have been right and never report one that was wrong.
    /// </remarks>
    private static bool DeclaresInnerResource(JsonElement node, bool isRoot)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    if (!isRoot && property.Name == "$id" && property.Value.ValueKind is JsonValueKind.String)
                    {
                        return true;
                    }

                    if (DeclaresInnerResource(property.Value, isRoot: false))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Array:
                return node.EnumerateArray().Any(item => DeclaresInnerResource(item, isRoot: false));

            default:
                return false;
        }
    }

    /// <summary>
    /// Visits the places a schema may sit, and only those: a <c>$ref</c> inside <c>const</c>,
    /// <c>enum</c>, <c>default</c> or <c>examples</c> is data that happens to look like one.
    /// </summary>
    private static void WalkSchema(JsonElement node, JsonElement root, string path, List<SchemaDefect> found)
    {
        if (node.ValueKind is not JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in node.EnumerateObject())
        {
            var here = $"{path}/{Escape(property.Name)}";

            if (property.Name == "$ref" && property.Value.ValueKind is JsonValueKind.String)
            {
                var problem = Unresolved(root, property.Value.GetString()!);
                if (problem is not null)
                {
                    found.Add(new SchemaDefect(here, SchemaDefectKinds.ReferenceUnresolved, problem));
                }

                continue;
            }

            if (Draft202012Corrections.SchemaValued.Contains(property.Name))
            {
                WalkSchema(property.Value, root, here, found);
            }
            else if (Draft202012Corrections.SchemaMapValued.Contains(property.Name) &&
                     property.Value.ValueKind is JsonValueKind.Object)
            {
                foreach (var member in property.Value.EnumerateObject())
                {
                    WalkSchema(member.Value, root, $"{here}/{Escape(member.Name)}", found);
                }
            }
            else if (Draft202012Corrections.SchemaArrayValued.Contains(property.Name) &&
                     property.Value.ValueKind is JsonValueKind.Array)
            {
                var index = 0;
                foreach (var member in property.Value.EnumerateArray())
                {
                    WalkSchema(member, root, $"{here}/{index.ToString(CultureInfo.InvariantCulture)}", found);
                    index++;
                }
            }
        }
    }

    /// <summary>
    /// Why a reference leads nowhere, or null when it leads somewhere or when this cannot say.
    /// </summary>
    /// <remarks>
    /// Only a JSON Pointer fragment of this document is judged (<c>#</c>, <c>#/a/b</c>). A
    /// reference to another document, a <c>concordat://</c> address, a plain-name anchor
    /// (<c>#node</c>) and a pointer with an escape RFC 6901 does not define are all someone else's
    /// to resolve, and a refusal has to be certain.
    /// </remarks>
    private static string? Unresolved(JsonElement root, string reference)
    {
        if (reference.Length == 0 || reference[0] != '#' || (reference.Length > 1 && reference[1] != '/'))
        {
            return null;
        }

        var pointer = Uri.UnescapeDataString(reference[1..]);
        var current = root;
        var walked = "#";

        foreach (var raw in pointer.Split('/').Skip(1))
        {
            if (!TryUnescapeToken(raw, out var token))
            {
                return null;
            }

            switch (current.ValueKind)
            {
                case JsonValueKind.Object when current.TryGetProperty(token, out var member):
                    current = member;
                    break;

                case JsonValueKind.Array when IsArrayIndex(token, out var index) &&
                                              index < current.GetArrayLength():
                    current = current[index];
                    break;

                default:
                    return $"The $ref {Quote(reference)} points at nothing in this document: " +
                        $"{Quote(walked)} has no {(current.ValueKind is JsonValueKind.Array ? "item" : "member")} " +
                        $"{Quote(token)}. A validator cannot compile a schema with a reference it cannot follow.";
            }

            walked += "/" + Escape(token);
        }

        return null;
    }

    private static bool TryUnescapeToken(string raw, out string token)
    {
        token = raw;

        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '~' && (i + 1 == raw.Length || raw[i + 1] is not ('0' or '1')))
            {
                return false;
            }
        }

        // RFC 6901 §4: '~1' first, so that '~01' reads as '~1' and not '/'.
        token = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        return true;
    }

    /// <summary>RFC 6901 §4: an index is "0" or digits with no leading zero.</summary>
    private static bool IsArrayIndex(string token, out int index)
    {
        index = 0;

        return token.Length > 0 &&
            token.All(char.IsAsciiDigit) &&
            (token.Length == 1 || token[0] != '0') &&
            int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    /// <summary>A piece of the document, bounded and escaped, for a message sent to a caller.</summary>
    private static string Quote(string value) =>
        // JsonEncodedText and not JsonSerializer: the CLI is published NativeAOT, where reflection-based
        // serialisation is a build error (IL2026, IL3050), and this escapes the same way.
        $"\"{JsonEncodedText.Encode(value.Length <= 80 ? value : value[..80] + "…")}\"";

    // RFC 6901: '~' becomes '~0' and '/' becomes '~1'.
    private static string Escape(string token) =>
        token.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);
}
