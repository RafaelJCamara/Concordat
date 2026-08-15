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
    /// <summary>
    /// The largest payload this validator will parse, in UTF-8 bytes.
    /// </summary>
    /// <remarks>
    /// Both the schema and the message arriving on this path are attacker-controlled under
    /// this engine's threat model, and there is no upstream size limit between a RabbitMQ
    /// delivery and this call. Generous for any legitimate event payload — this is a message
    /// bus, not a file transfer — and cheap defence-in-depth independent of whatever the
    /// caller does or does not enforce itself.
    /// </remarks>
    public const int MaxPayloadBytes = 10 * 1024 * 1024;

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

        var payloadBytes = System.Text.Encoding.UTF8.GetByteCount(payload);
        if (payloadBytes > MaxPayloadBytes)
        {
            return new PayloadValidationResult(
                false,
                [new PayloadError(
                    "#",
                    "payload_too_large",
                    $"The payload is {payloadBytes} bytes; this validator parses at most " +
                    $"{MaxPayloadBytes} bytes.")]);
        }

        JsonSchema schema;
        try
        {
            schema = _compiled.GetOrAdd(
                canonicalSchema,
                // Boolean subschemas are rewritten to their object equivalents purely so this
                // library can compile them; the canonical text and its id are untouched.
                static text => JsonSchema
                    .FromJsonAsync(Draft202012Corrections.RewriteBooleanSubschemas(text))
                    .GetAwaiter().GetResult());
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
            errors = ValidateWithHardTimeout(schema, canonicalSchema, payload);
        }
        catch (PatternMatchTimedOutException)
        {
            // A pattern keyword with catastrophic backtracking, matched against a payload
            // chosen to trigger it -- a ReDoS attempt, not a malformed document. The failure is
            // reported as a verdict for the same reason every other one here is: this runs on
            // the delivery path, where an exception is worse than a refusal. See
            // ValidateWithHardTimeout for why this is a hard wall-clock bound rather than a
            // dependency on RegexSafety having taken effect.
            return new PayloadValidationResult(
                false,
                [new PayloadError(
                    "#",
                    "pattern_match_timeout",
                    "A 'pattern' or 'patternProperties' regular expression took too long to " +
                    "match this payload and was aborted.")]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Malformed JSON arrives here rather than as a throw: this runs on the delivery
            // path, where an exception is a far worse failure than a verdict.
            return new PayloadValidationResult(
                false,
                [new PayloadError("#", "malformed_json", $"The payload is not well-formed JSON: {ex.Message}")]);
        }

        var corrected = CorrectToDraft202012(errors, payload, canonicalSchema);

        // NJsonSchema is over-strict on string length and over-permissive on enum, const and
        // uniqueItems. The first is filtered above; the second cannot be, because there is no
        // error to drop — the violation has to be found.
        var missed = Draft202012Corrections.FindMissedViolations(canonicalSchema, payload);

        if (corrected.Count == 0 && missed.Count == 0)
        {
            return PayloadValidationResult.Valid;
        }

        return new PayloadValidationResult(
            false,
            [
                .. corrected.Select(Translate),
                .. missed.Select(m => new PayloadError(m.Path, m.Kind, m.Message)),
            ]);
    }

    /// <summary>The most a pattern match may run before it is abandoned as a ReDoS attempt.</summary>
    private static readonly TimeSpan HardMatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs NJsonSchema's own validation with a hard wall-clock bound that does not depend on
    /// <see cref="RegexSafety.ApplyProcessWideDefault"/> having won the race described on that
    /// type.
    /// </summary>
    /// <exception cref="PatternMatchTimedOutException">
    /// The match ran past <see cref="HardMatchTimeout"/> and was abandoned.
    /// </exception>
    /// <remarks>
    /// Only schemas whose text mentions <c>pattern</c> pay for the dispatch to a second
    /// thread — every other schema, the overwhelming majority, validates exactly as it did
    /// before this existed, because the delivery path does not get slower for a threat a given
    /// schema cannot pose. A hung match is abandoned, not cancelled — NJsonSchema gives no way
    /// to cancel a synchronous match in progress — so the sacrificial thread keeps running
    /// until <see cref="RegexSafety"/>'s default cuts it off, if it won its race; either way,
    /// this method's caller is freed to keep processing other messages instead of staying
    /// blocked on this one forever.
    /// </remarks>
    private static ICollection<ValidationError> ValidateWithHardTimeout(
        JsonSchema schema, string canonicalSchema, string payload)
    {
        if (!canonicalSchema.Contains("pattern", StringComparison.Ordinal))
        {
            return schema.Validate(payload);
        }

        var task = Task.Run(() => schema.Validate(payload));

        if (!task.Wait(HardMatchTimeout))
        {
            throw new PatternMatchTimedOutException();
        }

        return task.GetAwaiter().GetResult();
    }

    /// <summary>Thrown by <see cref="ValidateWithHardTimeout"/> when it gives up waiting.</summary>
    private sealed class PatternMatchTimedOutException : Exception;

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
        ICollection<ValidationError> errors, string payload, string canonicalSchema)
    {
        var correctable = errors.Where(e =>
            e.Kind is ValidationErrorKind.IntegerExpected
                or ValidationErrorKind.StringTooLong
                or ValidationErrorKind.StringTooShort).ToList();

        if (correctable.Count == 0)
        {
            return [.. errors];
        }

        using var document = System.Text.Json.JsonDocument.Parse(payload);
        using var schema = System.Text.Json.JsonDocument.Parse(canonicalSchema);

        return [.. errors.Where(e => e.Kind switch
        {
            ValidationErrorKind.IntegerExpected =>
                !HasZeroFractionalPart(document.RootElement, e.Path),

            // NJsonSchema counts UTF-16 code units; 2020-12 counts characters. Anything outside
            // the Basic Multilingual Plane counts double, so an emoji fails maxLength 1 here and
            // passes in Python and Go.
            ValidationErrorKind.StringTooLong or ValidationErrorKind.StringTooShort =>
                !LengthIsActuallySatisfied(schema.RootElement, document.RootElement, e),

            _ => true,
        })];
    }

    private static bool LengthIsActuallySatisfied(
        System.Text.Json.JsonElement schemaRoot,
        System.Text.Json.JsonElement payloadRoot,
        ValidationError error)
    {
        if (Resolve(payloadRoot, error.Path) is not
            { ValueKind: System.Text.Json.JsonValueKind.String } value)
        {
            return false;
        }

        var isMaximum = error.Kind is ValidationErrorKind.StringTooLong;
        var keyword = isMaximum ? "maxLength" : "minLength";

        return FindBound(schemaRoot, error.Path, keyword) is { } bound &&
            Draft202012Corrections.LengthSatisfiedByCodePoints(value.GetString()!, bound, isMaximum);
    }

    /// <summary>Reads the length bound that applies at a payload path.</summary>
    /// <remarks>
    /// Walks <c>properties</c> and <c>items</c> alongside the payload path. It deliberately
    /// does not chase applicator keywords: a bound reached only through <c>oneOf</c> is already
    /// outside the interoperable subset that <c>JsonSchemaPortabilityChecker</c> warns about, so
    /// the correction stops exactly where the warning starts.
    /// </remarks>
    private static int? FindBound(
        System.Text.Json.JsonElement schema, string? path, string keyword)
    {
        var current = schema;

        foreach (var segment in Segments(path))
        {
            if (current.ValueKind is not System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            if (int.TryParse(segment, CultureInfo.InvariantCulture, out _))
            {
                if (!current.TryGetProperty("items", out var items))
                {
                    return null;
                }

                current = items;
                continue;
            }

            if (!current.TryGetProperty("properties", out var properties) ||
                !properties.TryGetProperty(segment, out var member))
            {
                return null;
            }

            current = member;
        }

        return current.ValueKind is System.Text.Json.JsonValueKind.Object &&
            current.TryGetProperty(keyword, out var bound) &&
            bound.ValueKind is System.Text.Json.JsonValueKind.Number &&
            bound.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static string[] Segments(string? path) =>
        string.IsNullOrEmpty(path) || path is "#"
            ? []
            : (path.StartsWith('#') ? path[1..] : path)
                .Replace("[", ".", StringComparison.Ordinal)
                .Replace("]", string.Empty, StringComparison.Ordinal)
                .Split(['.', '/'], StringSplitOptions.RemoveEmptyEntries);

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
