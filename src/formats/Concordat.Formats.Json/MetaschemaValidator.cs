using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Concordat.Formats.Json;

/// <summary>
/// Checks a JSON document against a set of meta-schemas, by interpreting the meta-schemas
/// themselves (ADR-039).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this reads the specification's own files instead of restating them.</b> "Valid under
/// the 2020-12 metaschema" is a statement about eight published documents. A hand-written list
/// of "<c>minimum</c> must be a number, <c>required</c> must be an array of unique strings, …"
/// is a transcription of them, and a transcription is where a refusal gets a rule slightly wrong
/// and turns away a schema that was entitled to register. Interpreting the files cannot drift
/// from them.
/// </para>
/// <para>
/// <b>Why it is not a general validator.</b> It implements the sixteen keywords those eight files
/// use to decide validity (and nine that only annotate) and nothing else. Anything outside that set, in a document it is given, throws when the
/// validator is built: a meta-schema that used a keyword this skipped would otherwise be checked
/// as if the keyword were not there, and every document would pass a rule that was never run.
/// The set is also why no third-party validator is a dependency: one that implements all of JSON
/// Schema (and can fetch what a <c>$ref</c> names) is a large surface for the one job of reading
/// eight fixed files.
/// </para>
/// <para>
/// <b>Two simplifications, each exact for these files and checked when they load.</b>
/// <c>$dynamicRef: "#meta"</c> resolves to the root meta-schema. The specification resolves it
/// to the outermost resource in the dynamic scope that carries <c>$dynamicAnchor: meta</c>, and
/// every document here does, and validation always enters at the root, so the outermost one is
/// the root. And <c>uniqueItems</c> compares string items only: every array it governs in these
/// files also requires its items to be strings, so a non-string item is already a defect and
/// whether it repeats cannot change the verdict. That is what keeps it linear, where comparing
/// every pair of a 512 KiB document's <c>required</c> list would not be.
/// </para>
/// <para>
/// <b><c>format</c> is an annotation.</b> The files say <c>format: uri</c>, <c>uri-reference</c>
/// and <c>regex</c>, and the specification does not make a validator assert a format unless it
/// is asked to; the published 2020-12 meta-schema itself includes the format <em>annotation</em>
/// vocabulary, not the assertion one. Asserting <c>regex</c> would also make the registry's
/// answer depend on whose regular-expression syntax is applied (ECMA-262's, the specification's,
/// or .NET's), which is the sort of disagreement between implementations this registry exists to
/// warn about and not to adjudicate.
/// </para>
/// </remarks>
internal sealed class MetaschemaValidator
{
    /// <summary>The longest piece of the document a message will quote, in characters.</summary>
    private const int MaxQuoted = 80;

    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    /// <summary>The keywords that decide validity. Everything the files use, and nothing more.</summary>
    private static readonly HashSet<string> Enforced = new(StringComparer.Ordinal)
    {
        "type", "enum", "minimum", "exclusiveMinimum", "minItems", "uniqueItems", "pattern",
        "properties", "additionalProperties", "propertyNames", "items", "allOf", "anyOf",
        "$ref", "$dynamicRef", "$defs",
    };

    /// <summary>Keywords that say something about a schema and decide nothing.</summary>
    private static readonly HashSet<string> Annotations = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "$vocabulary", "$dynamicAnchor", "$comment", "title",
        "default", "deprecated", "format",
    };

    private static readonly string[] PermittedTypes =
        ["array", "boolean", "integer", "null", "number", "object", "string"];

    private readonly Dictionary<string, JsonElement> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Regex> _patterns = new(StringComparer.Ordinal);
    private readonly string _rootUri;

    /// <summary>Reads the documents, and refuses any it cannot interpret exactly.</summary>
    /// <param name="documents">The meta-schema texts, in any order.</param>
    /// <param name="rootUri">The <c>$id</c> of the one validation starts from.</param>
    /// <exception cref="NotSupportedException">A document uses a keyword this does not implement.</exception>
    /// <exception cref="InvalidOperationException">A reference in a document points at nothing.</exception>
    public MetaschemaValidator(IEnumerable<string> documents, string rootUri)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootUri);

        _rootUri = rootUri;

        foreach (var text in documents)
        {
            // Never disposed: the parsed documents live exactly as long as the validator, which is
            // the process (see Draft202012Metaschema), and a JsonElement is invalid once its
            // document is.
            var document = JsonDocument.Parse(text);
            var id = document.RootElement.GetProperty("$id").GetString()!;
            _resources.Add(id, document.RootElement);
        }

        if (!_resources.ContainsKey(rootUri))
        {
            throw new InvalidOperationException($"The root meta-schema '{rootUri}' was not supplied.");
        }

        foreach (var (uri, root) in _resources)
        {
            Verify(root, uri, uri);
        }
    }

    /// <summary>Checks a document against the root meta-schema.</summary>
    /// <param name="instance">The document under test. Never interpreted as a schema.</param>
    /// <returns>Each defect as a JSON Pointer (prefixed <c>#</c>) and a message; empty when valid.</returns>
    public IReadOnlyList<(string Path, string Message)> Validate(JsonElement instance)
    {
        var errors = new List<(string, string)>();

        Evaluate(_resources[_rootUri], _rootUri, instance, string.Empty, errors);

        return [.. errors
            .Select(e => ("#" + e.Item1, e.Item2))
            .Distinct()];
    }

    // ------------------------------------------------------------------ loading

    /// <summary>
    /// Walks a meta-schema in its schema positions and throws on anything it could not
    /// interpret exactly.
    /// </summary>
    private void Verify(JsonElement schema, string resource, string where)
    {
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return;
        }

        if (schema.ValueKind is not JsonValueKind.Object)
        {
            throw new NotSupportedException($"{where}: a schema must be an object or a boolean.");
        }

        foreach (var keyword in schema.EnumerateObject())
        {
            var here = $"{where}/{keyword.Name}";

            if (Annotations.Contains(keyword.Name))
            {
                continue;
            }

            if (!Enforced.Contains(keyword.Name))
            {
                throw new NotSupportedException(
                    $"{here}: the keyword '{keyword.Name}' is not implemented, so a document " +
                    "would pass a rule that is never run.");
            }

            switch (keyword.Name)
            {
                case "items" or "additionalProperties" or "propertyNames":
                    Verify(keyword.Value, resource, here);
                    break;

                case "properties" or "$defs":
                    foreach (var member in keyword.Value.EnumerateObject())
                    {
                        Verify(member.Value, resource, $"{here}/{member.Name}");
                    }

                    break;

                case "allOf" or "anyOf":
                    var index = 0;
                    foreach (var branch in keyword.Value.EnumerateArray())
                    {
                        Verify(branch, resource, $"{here}/{index++}");
                    }

                    break;

                case "$ref":
                    // Resolved now, so that a reference to nothing fails the build of the
                    // validator and not the first document that happens to reach it.
                    _ = Resolve(keyword.Value.GetString()!, resource);
                    break;

                case "$dynamicRef":
                    if (keyword.Value.GetString() != "#meta")
                    {
                        throw new NotSupportedException(
                            $"{here}: only '#meta' is implemented (see the type remarks).");
                    }

                    break;

                case "enum":
                    if (keyword.Value.EnumerateArray().Any(m => m.ValueKind is not JsonValueKind.String))
                    {
                        throw new NotSupportedException($"{here}: only string members are implemented.");
                    }

                    break;

                case "minimum" or "exclusiveMinimum":
                    if (keyword.Value.ValueKind is not JsonValueKind.Number ||
                        NumberSign(keyword.Value.GetRawText()) != 0)
                    {
                        throw new NotSupportedException($"{here}: only a bound of 0 is implemented.");
                    }

                    break;

                case "pattern":
                    _patterns[keyword.Value.GetString()!] = Compile(keyword.Value.GetString()!);
                    break;

                case "type":
                    foreach (var name in TypeNames(keyword.Value))
                    {
                        if (!PermittedTypes.Contains(name, StringComparer.Ordinal))
                        {
                            throw new NotSupportedException($"{here}: unknown type '{name}'.");
                        }
                    }

                    break;

                case "uniqueItems":
                    if (keyword.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        throw new NotSupportedException($"{here}: must be a boolean.");
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// The metaschema's patterns end in <c>$</c>, which .NET lets match before a final line feed
    /// and ECMA-262 does not, so <c>"abc\n"</c> would pass as an anchor name here and be refused
    /// by every validator that follows the specification. <c>\z</c> is the end of the text and
    /// nothing else.
    /// </summary>
    private static Regex Compile(string pattern)
    {
        var ecmaEnd = pattern.EndsWith('$') && !pattern.EndsWith("\\$", StringComparison.Ordinal)
            ? string.Concat(pattern.AsSpan(0, pattern.Length - 1), "\\z")
            : pattern;

        return new Regex(ecmaEnd, RegexOptions.CultureInvariant, PatternTimeout);
    }

    // ---------------------------------------------------------------- evaluating

    private void Evaluate(
        JsonElement schema,
        string resource,
        JsonElement instance,
        string pointer,
        List<(string, string)> errors)
    {
        if (schema.ValueKind is JsonValueKind.True)
        {
            return;
        }

        if (schema.ValueKind is JsonValueKind.False)
        {
            errors.Add((pointer, "is not allowed"));
            return;
        }

        foreach (var keyword in schema.EnumerateObject())
        {
            switch (keyword.Name)
            {
                case "type":
                    EvaluateType(keyword.Value, instance, pointer, errors);
                    break;

                case "enum":
                    if (instance.ValueKind is not JsonValueKind.String ||
                        !keyword.Value.EnumerateArray().Any(m => m.GetString() == instance.GetString()))
                    {
                        // Says what was found as well as what was allowed: "bogus" in the message is
                        // the difference between a defect an author can find and one they have to
                        // go and read the document to locate.
                        var found = instance.ValueKind is JsonValueKind.String
                            ? Quote(instance.GetString()!)
                            : Article(KindOf(instance));

                        errors.Add((pointer, "must be one of " + string.Join(
                            ", ", keyword.Value.EnumerateArray().Select(m => m.GetString())) +
                            $", not {found}"));
                    }

                    break;

                case "minimum" when instance.ValueKind is JsonValueKind.Number:
                    if (NumberSign(instance.GetRawText()) < 0)
                    {
                        errors.Add((pointer, "must be at least 0"));
                    }

                    break;

                case "exclusiveMinimum" when instance.ValueKind is JsonValueKind.Number:
                    if (NumberSign(instance.GetRawText()) <= 0)
                    {
                        errors.Add((pointer, "must be greater than 0"));
                    }

                    break;

                case "minItems" when instance.ValueKind is JsonValueKind.Array:
                    if (instance.GetArrayLength() < keyword.Value.GetInt32())
                    {
                        errors.Add((pointer, $"must have at least {keyword.Value.GetInt32()} item(s)"));
                    }

                    break;

                case "uniqueItems" when instance.ValueKind is JsonValueKind.Array &&
                                        keyword.Value.ValueKind is JsonValueKind.True:
                    var repeated = FirstRepeatedString(instance);
                    if (repeated is not null)
                    {
                        errors.Add((pointer,
                            $"must not repeat an item, and {Quote(repeated)} appears more than once"));
                    }

                    break;

                case "pattern" when instance.ValueKind is JsonValueKind.String:
                    EvaluatePattern(schema, keyword.Value.GetString()!, instance, pointer, errors);
                    break;

                case "properties" when instance.ValueKind is JsonValueKind.Object:
                    foreach (var member in keyword.Value.EnumerateObject())
                    {
                        if (instance.TryGetProperty(member.Name, out var value))
                        {
                            Evaluate(member.Value, resource, value, pointer + "/" + Escape(member.Name), errors);
                        }
                    }

                    break;

                case "additionalProperties" when instance.ValueKind is JsonValueKind.Object:
                    schema.TryGetProperty("properties", out var declared);
                    foreach (var property in instance.EnumerateObject())
                    {
                        if (declared.ValueKind is JsonValueKind.Object &&
                            declared.TryGetProperty(property.Name, out _))
                        {
                            continue;
                        }

                        Evaluate(
                            keyword.Value, resource, property.Value,
                            pointer + "/" + Escape(property.Name), errors);
                    }

                    break;

                case "propertyNames" when instance.ValueKind is JsonValueKind.Object:
                    foreach (var property in instance.EnumerateObject())
                    {
                        using var name = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(property.Name));
                        var found = new List<(string, string)>();

                        Evaluate(keyword.Value, resource, name.RootElement, string.Empty, found);

                        errors.AddRange(found.Select(f =>
                            (pointer + "/" + Escape(property.Name), "has a name that " + f.Item2)));
                    }

                    break;

                case "items" when instance.ValueKind is JsonValueKind.Array:
                    var position = 0;
                    foreach (var element in instance.EnumerateArray())
                    {
                        Evaluate(
                            keyword.Value, resource, element,
                            pointer + "/" + position.ToString(CultureInfo.InvariantCulture), errors);
                        position++;
                    }

                    break;

                case "allOf":
                    foreach (var branch in keyword.Value.EnumerateArray())
                    {
                        Evaluate(branch, resource, instance, pointer, errors);
                    }

                    break;

                case "anyOf":
                    EvaluateAnyOf(keyword.Value, resource, instance, pointer, errors);
                    break;

                case "$ref":
                    var (target, targetResource) = Resolve(keyword.Value.GetString()!, resource);
                    Evaluate(target, targetResource, instance, pointer, errors);
                    break;

                case "$dynamicRef":
                    Evaluate(_resources[_rootUri], _rootUri, instance, pointer, errors);
                    break;
            }
        }
    }

    private static void EvaluateType(
        JsonElement expected, JsonElement instance, string pointer, List<(string, string)> errors)
    {
        var names = TypeNames(expected).ToArray();

        if (names.Any(n => IsType(n, instance)))
        {
            return;
        }

        errors.Add((pointer, $"must be {string.Join(" or ", names.Select(Article))}, not {Article(KindOf(instance))}"));
    }

    private void EvaluatePattern(
        JsonElement schema, string pattern, JsonElement instance, string pointer, List<(string, string)> errors)
    {
        bool matches;
        try
        {
            matches = _patterns[pattern].IsMatch(instance.GetString()!);
        }
        catch (RegexMatchTimeoutException)
        {
            // A pattern that cannot decide is not a match. The ones these files carry are linear,
            // so this is a guard against a future file and not something a document can reach.
            matches = false;
        }

        if (matches)
        {
            return;
        }

        // The file says why in a $comment beside the pattern where it matters ("Non-empty
        // fragments not allowed"), and that is a better message than the expression.
        var why = schema.TryGetProperty("$comment", out var comment) && comment.ValueKind is JsonValueKind.String
            ? $": {comment.GetString()!.TrimEnd('.')}"
            : string.Empty;

        errors.Add((pointer, $"must match the pattern {pattern}{why}"));
    }

    private void EvaluateAnyOf(
        JsonElement branches,
        string resource,
        JsonElement instance,
        string pointer,
        List<(string, string)> errors)
    {
        var reasons = new List<string>();

        foreach (var branch in branches.EnumerateArray())
        {
            var failed = new List<(string, string)>();
            Evaluate(branch, resource, instance, pointer, failed);

            if (failed.Count == 0)
            {
                return;
            }

            // Where the branch gave up, relative to here, so that "must be an array" and "the
            // value at /type is wrong" are not mistaken for the same finding.
            var (path, message) = failed[0];
            var relative = path[pointer.Length..];
            reasons.Add(relative.Length == 0 ? message : $"{message} (at {relative})");
        }

        errors.Add((pointer, "matches none of the permitted forms: " + string.Join("; or ", reasons.Distinct())));
    }

    // ------------------------------------------------------------------- helpers

    private (JsonElement Target, string Resource) Resolve(string reference, string resource)
    {
        var target = new Uri(new Uri(resource), reference);
        var resourceUri = target.GetLeftPart(UriPartial.Path);

        if (!_resources.TryGetValue(resourceUri, out var current))
        {
            throw new InvalidOperationException($"'{reference}' in '{resource}' names '{resourceUri}', which was not supplied.");
        }

        var fragment = Uri.UnescapeDataString(target.Fragment.TrimStart('#'));

        foreach (var token in fragment.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = token.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);

            if (current.ValueKind is not JsonValueKind.Object || !current.TryGetProperty(key, out current))
            {
                throw new InvalidOperationException($"'{reference}' in '{resource}' points at nothing.");
            }
        }

        return (current, resourceUri);
    }

    private static IEnumerable<string> TypeNames(JsonElement type) =>
        type.ValueKind is JsonValueKind.Array
            ? type.EnumerateArray().Select(t => t.GetString()!)
            : [type.GetString()!];

    private static bool IsType(string name, JsonElement instance) => name switch
    {
        "object" => instance.ValueKind is JsonValueKind.Object,
        "array" => instance.ValueKind is JsonValueKind.Array,
        "string" => instance.ValueKind is JsonValueKind.String,
        "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => instance.ValueKind is JsonValueKind.Null,
        "number" => instance.ValueKind is JsonValueKind.Number,
        "integer" => instance.ValueKind is JsonValueKind.Number && IsIntegral(instance.GetRawText()),
        _ => false,
    };

    private static string KindOf(JsonElement instance) => instance.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Number => "number",
        _ => "null",
    };

    private static string Article(string noun) =>
        noun is "array" or "integer" or "object" ? $"an {noun}" : noun is "null" ? "null" : $"a {noun}";

    /// <summary>The first string that occurs twice in an array, or null.</summary>
    private static string? FirstRepeatedString(JsonElement array)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind is JsonValueKind.String && !seen.Add(item.GetString()!))
            {
                return item.GetString();
            }
        }

        return null;
    }

    /// <summary>A value, bounded and escaped, for a message that is sent to a caller.</summary>
    private static string Quote(string value) =>
        JsonSerializer.Serialize(value.Length <= MaxQuoted ? value : value[..MaxQuoted] + "…");

    // RFC 6901: '~' becomes '~0' and '/' becomes '~1'.
    private static string Escape(string token) =>
        token.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>
    /// Whether a JSON number has a zero fractional part, however it is written: <c>1</c>,
    /// <c>1.0</c>, <c>10e-1</c> and <c>1e400</c> are integers, <c>1.5</c> and <c>1e-1</c> are not.
    /// </summary>
    /// <remarks>
    /// Read from the digits, not through a <see cref="double"/> or a <see cref="decimal"/>: the
    /// first rounds, and the second throws on <c>1e400</c>, and a schema may carry either.
    /// </remarks>
    internal static bool IsIntegral(string raw)
    {
        var text = raw.AsSpan().TrimStart('-');
        var e = text.IndexOfAny('e', 'E');
        var mantissa = e < 0 ? text : text[..e];

        var exponent = 0L;
        if (e >= 0 && !long.TryParse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
        {
            // An exponent too large for a long is a magnitude no fraction survives, or one that
            // shrinks every digit into the fraction. The sign says which.
            exponent = text[e + 1] == '-' ? long.MinValue / 2 : long.MaxValue / 2;
        }

        var dot = mantissa.IndexOf('.');
        var digits = (dot < 0 ? mantissa.ToString() : string.Concat(mantissa[..dot], mantissa[(dot + 1)..]));
        var fraction = dot < 0 ? 0 : mantissa.Length - dot - 1;

        if (digits.All(c => c == '0'))
        {
            return true;
        }

        var scale = fraction - exponent;

        if (scale <= 0)
        {
            return true;
        }

        return scale < digits.Length && digits[^(int)scale..].All(c => c == '0');
    }

    /// <summary>The sign of a JSON number: -1, 0 or 1. Negative zero is zero.</summary>
    internal static int NumberSign(string raw)
    {
        var negative = raw.StartsWith('-');
        var e = raw.IndexOfAny(['e', 'E']);
        var mantissa = raw[(negative ? 1 : 0)..(e < 0 ? raw.Length : e)];

        if (mantissa.All(c => c is '0' or '.'))
        {
            return 0;
        }

        return negative ? -1 : 1;
    }
}
