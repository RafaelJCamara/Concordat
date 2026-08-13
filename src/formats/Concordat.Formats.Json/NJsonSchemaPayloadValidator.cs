using System.Collections.Concurrent;
using System.Globalization;
using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;
using NJsonSchema;
using NJsonSchema.Validation;

namespace Concordat.Formats.Json;

/// <summary>
/// Validates JSON payloads with NJsonSchema.
/// </summary>
/// <remarks>
/// <para>
/// NJsonSchema is <b>MIT</b>, which is why it is here. The better-known
/// <c>JsonSchema.Net</c> has MIT source but publishes its NuGet binary under an Open Source
/// Maintenance Fee agreement charging revenue-generating users above US$10,000 annual
/// revenue — an obligation that would propagate to Concordat's own users and breach ADR-009.
/// <c>Newtonsoft.Json.Schema</c> is commercially licensed outright.
/// </para>
/// <para>
/// Parsed schemas are cached by canonical text. Compiling a schema is the expensive part and
/// this sits on the delivery path, where DESIGN §5's hard rule is that nothing may be slow
/// after warm-up. The key is the canonical text rather than the schema id so the cache works
/// before an id has been computed.
/// </para>
/// </remarks>
public sealed class NJsonSchemaPayloadValidator : IPayloadValidator
{
    private readonly ConcurrentDictionary<string, JsonSchema> _compiled = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public SchemaFormat Format => SchemaFormat.Json;

    /// <inheritdoc />
    public PayloadValidationResult Validate(string canonicalSchema, string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSchema);

        if (string.IsNullOrWhiteSpace(payload))
        {
            return new PayloadValidationResult(
                false, [new PayloadError("#", "empty", "The payload is empty.")]);
        }

        JsonSchema schema;
        try
        {
            schema = _compiled.GetOrAdd(
                canonicalSchema,
                static text => JsonSchema.FromJsonAsync(text).GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // A schema that will not compile is a registry-side problem, but discovering it
            // here must not take the consumer down with it.
            return new PayloadValidationResult(
                false,
                [new PayloadError("#", "schema_uncompilable", $"The schema could not be compiled: {ex.Message}")]);
        }

        ICollection<ValidationError> errors;
        try
        {
            errors = schema.Validate(payload);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Malformed JSON arrives here rather than as a throw: this runs on the delivery
            // path, where an exception is a far worse failure than a verdict.
            return new PayloadValidationResult(
                false,
                [new PayloadError("#", "malformed_json", $"The payload is not well-formed JSON: {ex.Message}")]);
        }

        var corrected = CorrectToDraft202012(errors, payload);

        return corrected.Count == 0
            ? PayloadValidationResult.Valid
            : new PayloadValidationResult(false, [.. corrected.Select(Translate)]);
    }

    /// <summary>
    /// Drops errors NJsonSchema raises that draft 2020-12 does not consider errors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>IntegerExpected</c> on a number with a zero fractional part.</b> NJsonSchema
    /// implements draft-04 semantics, where <c>integer</c> means an integral JSON token. From
    /// draft-06 onward — and so in 2020-12, the dialect ADR-021 pins — <c>integer</c> "matches
    /// any number with a zero fractional part", which makes <c>1.0</c> valid and <c>1.5</c>
    /// invalid.
    /// </para>
    /// <para>
    /// The conformance corpus caught this on the first run. Correcting it here rather than
    /// relaxing the fixture is the whole point of the port: the corpus is normative, and an
    /// implementation that disagrees with it is the thing that must bend. A JavaScript
    /// producer emitting <c>1.0</c> for a whole number — which is what <c>JSON.stringify</c>
    /// does for some values — would otherwise be quarantined by the .NET consumer and accepted
    /// by every other SDK.
    /// </para>
    /// </remarks>
    private static List<ValidationError> CorrectToDraft202012(
        ICollection<ValidationError> errors, string payload)
    {
        if (errors.Count == 0 || errors.All(e => e.Kind is not ValidationErrorKind.IntegerExpected))
        {
            return [.. errors];
        }

        using var document = System.Text.Json.JsonDocument.Parse(payload);

        return [.. errors.Where(e =>
            e.Kind is not ValidationErrorKind.IntegerExpected ||
            !HasZeroFractionalPart(document.RootElement, e.Path))];
    }

    private static bool HasZeroFractionalPart(
        System.Text.Json.JsonElement root, string? path)
    {
        var element = Resolve(root, path);

        return element is { ValueKind: System.Text.Json.JsonValueKind.Number } value &&
            value.TryGetDecimal(out var number) &&
            number == decimal.Truncate(number);
    }

    private static System.Text.Json.JsonElement? Resolve(
        System.Text.Json.JsonElement root, string? path)
    {
        if (string.IsNullOrEmpty(path) || path is "#")
        {
            return root;
        }

        var current = root;

        foreach (var segment in (path.StartsWith('#') ? path[1..] : path)
                     .Replace("[", ".", StringComparison.Ordinal)
                     .Replace("]", string.Empty, StringComparison.Ordinal)
                     .Split(['.', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            switch (current.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object
                    when current.TryGetProperty(segment, out var property):
                    current = property;
                    break;

                case System.Text.Json.JsonValueKind.Array
                    when int.TryParse(segment, CultureInfo.InvariantCulture, out var index) &&
                         index >= 0 && index < current.GetArrayLength():
                    current = current[index];
                    break;

                default:
                    return null;
            }
        }

        return current;
    }

    /// <summary>Turns an NJsonSchema error into Concordat's shape.</summary>
    /// <remarks>
    /// The library reports a dotted <c>Path</c> such as <c>#/order.id</c>. Concordat reports
    /// JSON Pointers everywhere else, so it is converted here rather than leaking a
    /// library-specific spelling into the protocol (ADR-019).
    /// </remarks>
    private static PayloadError Translate(ValidationError error) =>
        new(
            ToJsonPointer(error.Path),
            error.Kind.ToString(),
            $"{error.Kind} at {ToJsonPointer(error.Path)}"
            + (error.Property is { Length: > 0 } p ? $" (property '{p}')" : string.Empty));

    private static string ToJsonPointer(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "#")
        {
            return "#";
        }

        // NJsonSchema emits "#/a.b[0]"; JSON Pointer wants "#/a/b/0".
        var trimmed = path.StartsWith('#') ? path[1..] : path;
        trimmed = trimmed.TrimStart('/');

        var segments = trimmed
            .Replace("[", ".", StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal)
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s
                .Replace("~", "~0", StringComparison.Ordinal)
                .Replace("/", "~1", StringComparison.Ordinal));

        return "#/" + string.Join('/', segments);
    }

    /// <summary>The number of schemas currently compiled and cached.</summary>
    /// <remarks>Exposed so a test can prove compilation happens once, not per message.</remarks>
    internal int CompiledCount => _compiled.Count;

    /// <summary>Formats a count for diagnostics.</summary>
    /// <returns>A short description of the cache state.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"NJsonSchema validator ({_compiled.Count} compiled)");
}
