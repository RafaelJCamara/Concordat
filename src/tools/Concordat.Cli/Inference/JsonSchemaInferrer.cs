using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Concordat.Cli.Inference;

/// <summary>
/// Drafts a JSON Schema from real messages (ADR-014).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every rule here is a guess, and the report says which.</b> Inference exists because the
/// realistic adopter has 50 to 500 untyped message types and no schemas, and hand-authoring
/// those is weeks of work before the product delivers anything. The goal is to turn that into
/// an afternoon of review — not to be right unaided.
/// </para>
/// <para>
/// Deliberately <b>not</b> corpus-pinned, unlike canonicalisation or compatibility. Those are
/// protocol and every SDK must agree to the byte. This is a drafting aid whose output a human
/// reads and edits, so a Python implementation inferring slightly differently costs nothing.
/// </para>
/// </remarks>
public sealed partial class JsonSchemaInferrer
{
    /// <summary>Below this, everything inferred is thin evidence.</summary>
    public const int ThinEvidenceThreshold = 10;

    /// <summary>The most distinct values that can still read as an enum.</summary>
    public const int MaxEnumCardinality = 12;

    /// <summary>Distinct string values retained per node, to bound memory on a large drain.</summary>
    private const int StringSampleCap = 256;

    /// <summary>Infers a schema from a set of documents.</summary>
    /// <param name="samples">The payloads, as text.</param>
    /// <returns>The draft and its findings.</returns>
    /// <exception cref="InvalidOperationException">No sample could be parsed.</exception>
    public static InferenceResult Infer(IReadOnlyCollection<string> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var root = new Node();
        var parsed = 0;

        foreach (var sample in samples)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(sample);
            }
            catch (JsonException)
            {
                // Skipped rather than fatal. A drain of a live queue will pick up the odd
                // non-JSON message, and refusing to infer anything because of one is useless.
                continue;
            }

            using (document)
            {
                root.Observe(document.RootElement);
                parsed++;
            }
        }

        if (parsed == 0)
        {
            throw new InvalidOperationException("No sample could be parsed as JSON.");
        }

        var findings = new List<InferenceFinding>();

        if (parsed < ThinEvidenceThreshold)
        {
            findings.Add(new InferenceFinding(
                "#",
                Confidence.Low,
                FindingKinds.ThinEvidence,
                $"Only {parsed} sample(s). Optional fields that happen to appear in all of them " +
                "were inferred as required, and any field absent from all of them is missing " +
                "entirely. Treat the whole draft as a starting point."));
        }

        var schema = root.ToSchema("#", parsed, findings);

        // Worst first: a reviewer with limited attention should spend it where the guess is
        // weakest.
        findings.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));

        return new InferenceResult(
            schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            parsed,
            findings);
    }

    [GeneratedRegex(
        "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Uuid();

    [GeneratedRegex(
        @"^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(\.\d+)?([Zz]|[+-]\d{2}:\d{2})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DateTime();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex Date();

    [GeneratedRegex(@"^[^@\s]+@[^@\s.]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    /// <summary>What was seen at one position across every sample.</summary>
    private sealed class Node
    {
        private readonly HashSet<JsonValueKind> _kinds = [];
        private readonly Dictionary<string, Node> _properties = new(StringComparer.Ordinal);
        private readonly HashSet<string> _strings = [];

        private Node? _items;
        private int _stringCount;
        private int _numberCount;
        private int _wholeNumberCount;
        private bool _stringsTruncated;
        private bool _sawNonEmptyArray;

        /// <summary>How many times this position was present.</summary>
        public int Occurrences { get; private set; }

        /// <summary>Present every time, but never with a value.</summary>
        public bool IsAlwaysNull => _kinds.Count > 0 && _kinds.All(k => k is JsonValueKind.Null);

        public void Observe(JsonElement element)
        {
            Occurrences++;
            _kinds.Add(element.ValueKind);

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (!_properties.TryGetValue(property.Name, out var child))
                        {
                            child = new Node();
                            _properties[property.Name] = child;
                        }

                        child.Observe(property.Value);
                    }

                    break;

                case JsonValueKind.Array:
                    _items ??= new Node();

                    foreach (var item in element.EnumerateArray())
                    {
                        _sawNonEmptyArray = true;
                        _items.Observe(item);
                    }

                    break;

                case JsonValueKind.String:
                    _stringCount++;

                    if (_strings.Count < StringSampleCap)
                    {
                        _strings.Add(element.GetString() ?? string.Empty);
                    }
                    else
                    {
                        _stringsTruncated = true;
                    }

                    break;

                case JsonValueKind.Number:
                    _numberCount++;

                    // The integer/number call is the one most likely to be wrong, and its cost
                    // is asymmetric: guessing integer rejects a later 1.5, while guessing
                    // number accepts everything. It is narrowed anyway, and reported.
                    if (element.TryGetInt64(out _))
                    {
                        _wholeNumberCount++;
                    }

                    break;

                default:
                    break;
            }
        }

        public JsonObject ToSchema(string path, int parentOccurrences, List<InferenceFinding> findings)
        {
            var schema = new JsonObject();
            var nullable = _kinds.Contains(JsonValueKind.Null);
            var concrete = _kinds.Where(k => k is not JsonValueKind.Null).ToList();

            if (concrete.Count == 0)
            {
                findings.Add(new InferenceFinding(
                    path,
                    Confidence.Low,
                    FindingKinds.AlwaysNull,
                    "Only ever null, so nothing is known about its type. Left unconstrained."));

                return schema;
            }

            if (concrete.Count > 1)
            {
                findings.Add(new InferenceFinding(
                    path,
                    Confidence.Low,
                    FindingKinds.MixedTypes,
                    $"Held more than one JSON type across samples ({string.Join(", ", concrete)}). " +
                    "Left unconstrained rather than guessing which is correct."));

                return schema;
            }

            var kind = concrete[0];
            var typeName = TypeName(kind, path, findings);

            schema["type"] = nullable
                ? new JsonArray(JsonValue.Create(typeName), JsonValue.Create("null"))
                : JsonValue.Create(typeName);

            switch (kind)
            {
                case JsonValueKind.Object:
                    AddObject(schema, path, findings);
                    break;

                case JsonValueKind.Array when _sawNonEmptyArray && _items is not null:
                    schema["items"] = _items.ToSchema($"{path}/items", _items.Occurrences, findings);
                    break;

                case JsonValueKind.Array:
                    findings.Add(new InferenceFinding(
                        path,
                        Confidence.Low,
                        FindingKinds.EmptyArray,
                        "Always empty, so nothing is known about its items. Left unconstrained."));
                    break;

                case JsonValueKind.String:
                    AddStringConstraints(schema, path, findings);
                    break;

                default:
                    break;
            }

            return schema;
        }

        private string TypeName(JsonValueKind kind, string path, List<InferenceFinding> findings)
        {
            switch (kind)
            {
                case JsonValueKind.Object: return "object";
                case JsonValueKind.Array: return "array";
                case JsonValueKind.String: return "string";
                case JsonValueKind.True:
                case JsonValueKind.False: return "boolean";

                case JsonValueKind.Number when _numberCount > 0 && _wholeNumberCount == _numberCount:
                    findings.Add(new InferenceFinding(
                        path,
                        _numberCount >= ThinEvidenceThreshold ? Confidence.Medium : Confidence.Low,
                        FindingKinds.IntegerFromWholeNumbers,
                        $"All {_numberCount} observed value(s) were whole, so this is typed as " +
                        "integer. If the field can ever be fractional, a later 1.5 will be " +
                        "rejected — widen it to number."));

                    return "integer";

                default: return "number";
            }
        }

        private void AddObject(JsonObject schema, string path, List<InferenceFinding> findings)
        {
            if (_properties.Count == 0)
            {
                return;
            }

            var properties = new JsonObject();
            var required = new JsonArray();

            foreach (var (name, child) in _properties.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var childPath = $"{path}/properties/{name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)}";
                properties[name] = child.ToSchema(childPath, child.Occurrences, findings);

                if (child.Occurrences == Occurrences)
                {
                    required.Add((JsonNode)JsonValue.Create(name));

                    // A key that is always present but always null is the weakest case of all:
                    // "required, of unknown type" is a combination almost nobody means, and it
                    // usually indicates an optional field the samples never exercised.
                    var alwaysNull = child.IsAlwaysNull;

                    findings.Add(new InferenceFinding(
                        childPath,
                        alwaysNull || Occurrences < ThinEvidenceThreshold
                            ? Confidence.Low
                            : Confidence.Medium,
                        FindingKinds.RequiredFromPresence,
                        alwaysNull
                            ? $"Present in all {Occurrences} sample(s) but never with a value, so " +
                              "inferred required with no type. That combination is rarely intended — " +
                              "it usually means an optional field the samples never exercised."
                            : $"Present in all {Occurrences} sample(s), so inferred required. An " +
                              "optional field that happens always to be set looks identical from here."));
                }
                else
                {
                    findings.Add(new InferenceFinding(
                        childPath,
                        Confidence.High,
                        FindingKinds.OptionalFromAbsence,
                        $"Present in {child.Occurrences} of {Occurrences} sample(s), so optional."));
                }
            }

            schema["properties"] = properties;

            if (required.Count > 0)
            {
                schema["required"] = required;
            }

            // additionalProperties is deliberately not emitted. The default is open, which is
            // both JSON Schema's default and Concordat's (ContentModel.Open), and closing a
            // model inferred from samples would reject every field the samples happened to
            // miss.
        }

        private void AddStringConstraints(JsonObject schema, string path, List<InferenceFinding> findings)
        {
            if (_strings.Count == 0)
            {
                return;
            }

            var format = DetectFormat();

            if (format is not null)
            {
                schema["format"] = format;

                findings.Add(new InferenceFinding(
                    path,
                    _stringCount >= ThinEvidenceThreshold ? Confidence.Medium : Confidence.Low,
                    FindingKinds.FormatFromPattern,
                    $"All {_stringCount} observed value(s) look like {format}. Note that under " +
                    "JSON Schema 2020-12 `format` is an annotation, not an assertion, unless " +
                    "the validator is configured to assert it."));

                return;
            }

            // A single distinct value is not an enum, it is a coincidence — every sample so far
            // happened to say the same thing. Emitting enum:["hello"] there would reject the
            // second value the field ever takes, which is the most damaging inference this
            // tool could make.
            if (!_stringsTruncated && _strings.Count == 1 && _stringCount > 1)
            {
                findings.Add(new InferenceFinding(
                    path,
                    Confidence.Medium,
                    FindingKinds.ConstantValue,
                    $"Every one of the {_stringCount} observed value(s) was \"{_strings.First()}\". " +
                    "Left unconstrained: one repeated value is not evidence of a closed set. Add " +
                    "an enum or const yourself if it genuinely is one."));

                return;
            }

            // An enum needs several distinct values, enough repetition to suggest the set is
            // closed, and enough samples for either to mean anything. Three distinct values
            // across three samples is not an enum, it is three samples.
            if (!_stringsTruncated
                && _strings.Count is > 1 and <= MaxEnumCardinality
                && _stringCount >= ThinEvidenceThreshold
                && _stringCount >= _strings.Count * 3)
            {
                var values = new JsonArray();

                foreach (var value in _strings.Order(StringComparer.Ordinal))
                {
                    values.Add((JsonNode)JsonValue.Create(value));
                }

                schema["enum"] = values;

                findings.Add(new InferenceFinding(
                    path,
                    Confidence.Low,
                    FindingKinds.EnumFromLowCardinality,
                    $"Only {_strings.Count} distinct value(s) across {_stringCount} observation(s), " +
                    "so inferred as an enum. A value that simply never appeared in the samples " +
                    "will be rejected — delete the enum if the set is not genuinely closed."));
            }
        }

        private string? DetectFormat()
        {
            if (_stringsTruncated || _strings.Count == 0 || _strings.Any(string.IsNullOrEmpty))
            {
                return null;
            }

            if (_strings.All(s => Uuid().IsMatch(s)))
            {
                return "uuid";
            }

            if (_strings.All(s => DateTime().IsMatch(s)))
            {
                return "date-time";
            }

            if (_strings.All(s => Date().IsMatch(s)))
            {
                return "date";
            }

            return _strings.All(s => Email().IsMatch(s)) ? "email" : null;
        }
    }

    /// <summary>Renders a report as text for a human.</summary>
    /// <param name="result">The inference result.</param>
    /// <returns>The lines to print.</returns>
    public static IEnumerable<string> Describe(InferenceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        yield return $"{result.SampleCount} sample(s) read.";

        if (result.Findings.Count == 0)
        {
            yield break;
        }

        yield return string.Empty;

        foreach (var finding in result.Findings)
        {
            var marker = finding.Confidence switch
            {
                Confidence.Low => "!!",
                Confidence.Medium => " !",
                _ => "  ",
            };

            yield return string.Create(
                CultureInfo.InvariantCulture, $"{marker} {finding.Path}  [{finding.Kind}]");
            yield return $"     {finding.Message}";
        }
    }
}
