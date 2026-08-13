using System.Text.Json;
using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;

namespace Concordat.Formats.Json;

/// <summary>
/// Reports where a JSON Schema leaves the interoperable subset (M6.1).
/// </summary>
/// <remarks>
/// <para>
/// Three classes of finding, each earning its place by naming a divergence that actually
/// happens:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Dialect.</b> Concordat implements draft 2020-12 and nothing else. A document
///     declaring draft-07 is <em>refused</em>, because keywords moved between those drafts —
///     <c>items</c> alone changed meaning — so accepting it would mean validating one document
///     under rules its author did not write it against.
///   </description></item>
///   <item><description>
///     <b>Keywords the compatibility engine does not compare.</b> These validate correctly;
///     they are simply invisible to compatibility, so a change confined to one reads as
///     compatible when it may not be. This is the hole recorded as DECISIONS-PENDING #9, and
///     warning is the answer M6.1 chose over silently under-reporting or refusing outright.
///   </description></item>
///   <item><description>
///     <b>Regular expressions that cannot run everywhere.</b> The sharpest real divergence in
///     the set: JSON Schema specifies ECMA-262 regular expressions, but the Go validator
///     ADR-021 commits to is built on <b>RE2, which has no lookaround and no backreferences at
///     all</b>. A pattern using them does not merely behave differently in Go — it fails to
///     compile. Nothing in the corpus can fix that; the author has to know.
///   </description></item>
/// </list>
/// </remarks>
public sealed class JsonSchemaPortabilityChecker : ISchemaPortabilityChecker
{
    /// <summary>The one dialect Concordat implements.</summary>
    /// <remarks>
    /// Both spellings are accepted because the specification publishes the identifier with a
    /// trailing slash and the wild accepts it without; treating them as different dialects
    /// would refuse a document for a URI detail its author never chose.
    /// </remarks>
    private static readonly string[] SupportedDialects =
    [
        "https://json-schema.org/draft/2020-12/schema",
        "https://json-schema.org/draft/2020-12/schema#",
    ];

    /// <summary>
    /// Keywords that validate but are not compared by <see cref="JsonSchemaDiff"/>.
    /// </summary>
    /// <remarks>
    /// Kept beside the engine it describes rather than in documentation, because the failure
    /// mode is the list drifting out of date after someone teaches the engine a new keyword and
    /// leaves a warning telling authors it is not understood.
    /// </remarks>
    private static readonly Dictionary<string, string> NotCompared = new(StringComparer.Ordinal)
    {
        ["oneOf"] = "composition",
        ["anyOf"] = "composition",
        ["allOf"] = "composition",
        ["not"] = "composition",
        ["if"] = "conditional application",
        ["then"] = "conditional application",
        ["else"] = "conditional application",
        ["dependentSchemas"] = "conditional application",
        ["dependentRequired"] = "conditional requiredness",
        ["patternProperties"] = "pattern-keyed properties",
        ["propertyNames"] = "property-name constraints",
        ["prefixItems"] = "positional array items",
        ["contains"] = "array containment",
        ["minContains"] = "array containment",
        ["maxContains"] = "array containment",
        ["unevaluatedProperties"] = "unevaluated members",
        ["unevaluatedItems"] = "unevaluated members",
        ["$dynamicRef"] = "dynamic references",
        ["$dynamicAnchor"] = "dynamic references",
    };

    /// <summary>Regex constructs RE2 cannot compile.</summary>
    private static readonly (string Token, string Name)[] UnportableRegex =
    [
        ("(?=", "lookahead"),
        ("(?!", "negative lookahead"),
        ("(?<=", "lookbehind"),
        ("(?<!", "negative lookbehind"),
    ];

    /// <inheritdoc />
    public SchemaFormat Format => SchemaFormat.Json;

    /// <inheritdoc />
    public IReadOnlyList<PortabilityFinding> Check(string canonicalBody)
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
            // Malformed input is the canonicaliser's to report, and it already has. Saying so
            // twice, in different words, only makes the real message harder to find.
            return [];
        }

        using (document)
        {
            var found = new List<PortabilityFinding>();

            CheckDialect(document.RootElement, found);
            Walk(document.RootElement, "#", found);

            return [.. found.OrderBy(f => f.Path, StringComparer.Ordinal)
                .ThenBy(f => f.Kind, StringComparer.Ordinal)];
        }
    }

    private static void CheckDialect(JsonElement root, List<PortabilityFinding> found)
    {
        if (root.ValueKind is not JsonValueKind.Object ||
            !root.TryGetProperty("$schema", out var dialect) ||
            dialect.ValueKind is not JsonValueKind.String)
        {
            // An absent $schema means 2020-12 by assumption. Warning about it would fire on
            // almost every schema anyone writes, and a warning that always fires is noise.
            return;
        }

        var declared = dialect.GetString()!;
        if (SupportedDialects.Contains(declared, StringComparer.Ordinal))
        {
            return;
        }

        found.Add(new PortabilityFinding(
            "#/$schema",
            PortabilityKinds.DialectUnsupported,
            PortabilitySeverity.Error,
            $"Dialect '{declared}' is not supported; Concordat implements JSON Schema draft " +
            "2020-12 only. Keywords changed meaning between drafts — 'items' most visibly — so " +
            "validating this document under 2020-12 would apply rules it was not written " +
            "against."));
    }

    private static void Walk(JsonElement node, string path, List<PortabilityFinding> found)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    var childPath = $"{path}/{Escape(property.Name)}";

                    if (NotCompared.TryGetValue(property.Name, out var feature))
                    {
                        found.Add(new PortabilityFinding(
                            childPath,
                            PortabilityKinds.KeywordNotCompared,
                            PortabilitySeverity.Warning,
                            $"'{property.Name}' ({feature}) is validated but not compared by the " +
                            "compatibility engine, so a change confined to it is reported as " +
                            "compatible even when it is not. Keep the constraints compatibility " +
                            "must see in the keywords it understands."));
                    }

                    if (string.Equals(property.Name, "pattern", StringComparison.Ordinal) ||
                        string.Equals(property.Name, "patternProperties", StringComparison.Ordinal))
                    {
                        CheckRegex(property, childPath, found);
                    }

                    Walk(property.Value, childPath, found);
                }

                return;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in node.EnumerateArray())
                {
                    Walk(item, $"{path}/{index}", found);
                    index++;
                }

                return;

            default:
                return;
        }
    }

    private static void CheckRegex(
        JsonProperty property, string path, List<PortabilityFinding> found)
    {
        // 'pattern' holds one expression; 'patternProperties' keys by expression.
        if (property.Value.ValueKind is JsonValueKind.String)
        {
            Inspect(property.Value.GetString()!, path, found);
            return;
        }

        if (property.Value.ValueKind is JsonValueKind.Object)
        {
            foreach (var entry in property.Value.EnumerateObject())
            {
                Inspect(entry.Name, $"{path}/{Escape(entry.Name)}", found);
            }
        }

        static void Inspect(string expression, string path, List<PortabilityFinding> found)
        {
            foreach (var (token, name) in UnportableRegex)
            {
                if (!expression.Contains(token, StringComparison.Ordinal))
                {
                    continue;
                }

                found.Add(new PortabilityFinding(
                    path,
                    PortabilityKinds.RegexNotPortable,
                    PortabilitySeverity.Warning,
                    $"This pattern uses {name}, which Go's RE2 engine cannot compile at all. " +
                    "A Go consumer will fail to build the validator rather than disagree with " +
                    "it, so the payload check is lost entirely on that SDK."));

                return;
            }

            if (expression.Contains("\\1", StringComparison.Ordinal) ||
                expression.Contains("\\2", StringComparison.Ordinal))
            {
                found.Add(new PortabilityFinding(
                    path,
                    PortabilityKinds.RegexNotPortable,
                    PortabilitySeverity.Warning,
                    "This pattern uses a backreference, which Go's RE2 engine cannot compile. " +
                    "A Go consumer will fail to build the validator rather than disagree with " +
                    "it, so the payload check is lost entirely on that SDK."));
            }
        }
    }

    // RFC 6901: '~' becomes '~0' and '/' becomes '~1'.
    private static string Escape(string token) =>
        token.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);
}
