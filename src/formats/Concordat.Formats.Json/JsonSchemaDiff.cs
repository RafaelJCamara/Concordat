using System.Globalization;
using System.Text.Json;
using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;

namespace Concordat.Formats.Json;

/// <summary>
/// Walks two JSON Schema documents and reports every way the new one diverges from the old.
/// </summary>
/// <remarks>
/// <para>
/// Direction follows DESIGN §7 exactly. <b>Backward</b> means the new schema reads data
/// written under the old one, so a divergence is backward-breaking when the new schema is
/// <em>narrower</em> — documents that were valid may now be rejected. <b>Forward</b> means the
/// old schema reads data written under the new one, so a divergence is forward-breaking when
/// the new schema is <em>wider</em>.
/// </para>
/// <para>
/// Surfaces follow the ADR-016 lattice. Almost every JSON Schema divergence is
/// <see cref="CompatibilitySurface.WireJson"/>: JSON is self-describing, so the bytes keep
/// decoding and only validation fails. A few are
/// <see cref="CompatibilitySurface.Source"/>-only — they leave validation untouched but change
/// generated code. <see cref="CompatibilitySurface.Wire"/> findings do not arise for JSON
/// Schema at all, which is why a <c>× Wire</c> policy is effectively no checking for this
/// format.
/// </para>
/// </remarks>
internal static class JsonSchemaDiff
{
    private static readonly string[] NumericLowerBounds = ["minimum", "exclusiveMinimum"];
    private static readonly string[] NumericUpperBounds = ["maximum", "exclusiveMaximum"];

    internal static List<Divergence> Compare(
        JsonElement previous,
        JsonElement proposed,
        ContentModel contentModel)
    {
        var found = new List<Divergence>();
        CompareNode(previous, proposed, "#", contentModel, found);
        return found;
    }

    internal sealed record Divergence(
        string Path,
        string Kind,
        CompatibilityDirection Direction,
        CompatibilitySurface Surface,
        string Message);

    private static void CompareNode(
        JsonElement previous,
        JsonElement proposed,
        string path,
        ContentModel contentModel,
        List<Divergence> found)
    {
        if (previous.ValueKind is not JsonValueKind.Object ||
            proposed.ValueKind is not JsonValueKind.Object)
        {
            return;
        }

        CompareType(previous, proposed, path, found);
        CompareEnum(previous, proposed, path, found);
        CompareRequired(previous, proposed, path, found);
        CompareNumericBounds(previous, proposed, path, found);
        CompareStringConstraints(previous, proposed, path, found);
        CompareAdditionalProperties(previous, proposed, path, found);
        CompareFormat(previous, proposed, path, found);
        CompareProperties(previous, proposed, path, contentModel, found);
        CompareItems(previous, proposed, path, contentModel, found);
    }

    // ------------------------------------------------------------------ type

    private static void CompareType(
        JsonElement previous, JsonElement proposed, string path, List<Divergence> found)
    {
        var before = TypeSet(previous);
        var after = TypeSet(proposed);

        if (before.Count == 0 || after.Count == 0)
        {
            return;
        }

        var removed = before.Except(after, StringComparer.Ordinal).ToList();
        var added = after.Except(before, StringComparer.Ordinal).ToList();

        // integer -> number is a widening that keeps every existing document valid, so it is
        // not a validation break in either direction. It does change generated code from an
        // integral type to a floating-point one. This is JSON Schema's int32 -> int64: the
        // exact case a single-axis model cannot express (ADR-016).
        var integerToNumber = removed is ["integer"] && added is ["number"];
        if (integerToNumber)
        {
            found.Add(new Divergence(
                path,
                BreakingChangeKinds.IntegerWidenedToNumber,
                CompatibilityDirection.Backward,
                CompatibilitySurface.Source,
                "'integer' widened to 'number'. Every existing document still validates, but " +
                "generated code changes from an integral to a floating-point type."));
            return;
        }

        if (removed.Count > 0)
        {
            found.Add(new Divergence(
                path,
                BreakingChangeKinds.TypeNarrowed,
                CompatibilityDirection.Backward,
                CompatibilitySurface.WireJson,
                $"Type(s) {Quote(removed)} no longer permitted; documents written under the " +
                "previous version using them will be rejected."));
        }

        if (added.Count > 0)
        {
            found.Add(new Divergence(
                path,
                BreakingChangeKinds.TypeWidened,
                CompatibilityDirection.Forward,
                CompatibilitySurface.WireJson,
                $"Type(s) {Quote(added)} newly permitted; readers on the previous version " +
                "will reject documents that use them."));
        }
    }

    private static HashSet<string> TypeSet(JsonElement schema)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!schema.TryGetProperty("type", out var type))
        {
            return set;
        }

        switch (type.ValueKind)
        {
            case JsonValueKind.String:
                set.Add(type.GetString()!);
                break;
            case JsonValueKind.Array:
                foreach (var item in type.EnumerateArray())
                {
                    if (item.ValueKind is JsonValueKind.String)
                    {
                        set.Add(item.GetString()!);
                    }
                }

                break;
            default:
                break;
        }

        return set;
    }

    // ------------------------------------------------------------------ enum

    private static void CompareEnum(
        JsonElement previous, JsonElement proposed, string path, List<Divergence> found)
    {
        if (!previous.TryGetProperty("enum", out var before) ||
            !proposed.TryGetProperty("enum", out var after) ||
            before.ValueKind is not JsonValueKind.Array ||
            after.ValueKind is not JsonValueKind.Array)
        {
            return;
        }

        var beforeValues = before.EnumerateArray().Select(e => e.GetRawText()).ToHashSet(StringComparer.Ordinal);
        var afterValues = after.EnumerateArray().Select(e => e.GetRawText()).ToHashSet(StringComparer.Ordinal);

        var removed = beforeValues.Except(afterValues, StringComparer.Ordinal).ToList();
        var added = afterValues.Except(beforeValues, StringComparer.Ordinal).ToList();

        if (removed.Count > 0)
        {
            found.Add(new Divergence(
                path,
                BreakingChangeKinds.EnumValueRemoved,
                CompatibilityDirection.Backward,
                CompatibilitySurface.WireJson,
                $"Enum value(s) {Quote(removed)} removed; documents written under the " +
                "previous version using them will be rejected."));
        }

        if (added.Count > 0)
        {
            found.Add(new Divergence(
                path,
                BreakingChangeKinds.EnumValueAdded,
                CompatibilityDirection.Forward,
                CompatibilitySurface.WireJson,
                $"Enum value(s) {Quote(added)} added; readers on the previous version will " +
                "reject documents that use them."));
        }
    }

    // -------------------------------------------------------------- required

    private static void CompareRequired(
        JsonElement previous, JsonElement proposed, string path, List<Divergence> found)
    {
        var before = StringArray(previous, "required");
        var after = StringArray(proposed, "required");

        foreach (var name in after.Except(before, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            found.Add(new Divergence(
                $"{path}/required",
                BreakingChangeKinds.RequiredFieldAdded,
                CompatibilityDirection.Backward,
                CompatibilitySurface.WireJson,
                $"'{name}' is now required; documents written under the previous version " +
                "that omit it will be rejected."));
        }

        foreach (var name in before.Except(after, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            found.Add(new Divergence(
                $"{path}/required",
                BreakingChangeKinds.RequiredFieldRemoved,
                CompatibilityDirection.Forward,
                CompatibilitySurface.WireJson,
                $"'{name}' is no longer required; readers on the previous version will " +
                "reject documents that omit it."));
        }
    }

    // ------------------------------------------------------------ properties

    private static void CompareProperties(
        JsonElement previous,
        JsonElement proposed,
        string path,
        ContentModel contentModel,
        List<Divergence> found)
    {
        var before = Properties(previous);
        var after = Properties(proposed);

        foreach (var (name, schema) in after.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (before.TryGetValue(name, out var previousSchema))
            {
                CompareNode(previousSchema, schema, $"{path}/properties/{Escape(name)}", contentModel, found);
                continue;
            }

            // Adding an optional property is fully compatible under an open content model —
            // the single most common schema change, and the one Confluent's defaults block.
            // Under a closed model the old reader rejects the new property, so it is reported.
            if (contentModel is ContentModel.Closed)
            {
                found.Add(new Divergence(
                    $"{path}/properties/{Escape(name)}",
                    BreakingChangeKinds.PropertyAdded,
                    CompatibilityDirection.Forward,
                    CompatibilitySurface.WireJson,
                    $"Property '{name}' added under a closed content model; readers on the " +
                    "previous version will reject documents that carry it."));
            }
        }

        foreach (var name in before.Keys.Except(after.Keys, StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            if (contentModel is ContentModel.Closed)
            {
                found.Add(new Divergence(
                    $"{path}/properties/{Escape(name)}",
                    BreakingChangeKinds.PropertyRemoved,
                    CompatibilityDirection.Backward,
                    CompatibilitySurface.WireJson,
                    $"Property '{name}' removed under a closed content model; documents " +
                    "written under the previous version that carry it will be rejected."));
            }
        }
    }

    private static void CompareItems(
        JsonElement previous,
        JsonElement proposed,
        string path,
        ContentModel contentModel,
        List<Divergence> found)
    {
        if (previous.TryGetProperty("items", out var before) &&
            proposed.TryGetProperty("items", out var after))
        {
            CompareNode(before, after, $"{path}/items", contentModel, found);
        }
    }

    // ---------------------------------------------------------------- bounds

    private static void CompareNumericBounds(
        JsonElement previous, JsonElement proposed, string path, List<Divergence> found)
    {
        foreach (var keyword in NumericUpperBounds)
        {
            CompareBound(previous, proposed, path, keyword, upper: true, found);
        }

        foreach (var keyword in NumericLowerBounds)
        {
            CompareBound(previous, proposed, path, keyword, upper: false, found);
        }
    }

    private static void CompareBound(
        JsonElement previous,
        JsonElement proposed,
        string path,
        string keyword,
        bool upper,
        List<Divergence> found)
    {
        var before = Number(previous, keyword);
        var after = Number(proposed, keyword);

        if (before is null && after is null)
        {
            return;
        }

        // Adding a bound where none existed narrows; removing one widens.
        var narrowed = (before, after) switch
        {
            (null, not null) => true,
            (not null, null) => false,
            _ => upper ? after < before : after > before,
        };

        var widened = (before, after) switch
        {
            (not null, null) => true,
            (null, not null) => false,
            _ => upper ? after > before : after < before,
        };

        if (narrowed)
        {
            found.Add(new Divergence(
                $"{path}/{keyword}",
                BreakingChangeKinds.NumericRangeNarrowed,
                CompatibilityDirection.Backward,
                CompatibilitySurface.WireJson,
                $"'{keyword}' tightened from {Show(before)} to {Show(after)}; documents " +
                "written under the previous version may now be rejected."));
        }
        else if (widened)
        {
            found.Add(new Divergence(
                $"{path}/{keyword}",
                BreakingChangeKinds.NumericRangeWidened,
                CompatibilityDirection.Forward,
                CompatibilitySurface.WireJson,
                $"'{keyword}' loosened from {Show(before)} to {Show(after)}; readers on the " +
                "previous version may reject documents written under this one."));
        }
    }

    private static void CompareStringConstraints(
        JsonElement previous, JsonElement proposed, string path, List<Divergence> found)
    {
        CompareBound(previous, proposed, path, "maxLength", upper: true, found);
        CompareBound(previous, proposed, path, "minLength", upper: false, found);
        CompareBound(previous, proposed, path, "maxItems", upper: true, found);
        CompareBound(previous, proposed, path, "minItems", upper: false, found);

        var before = Text(previous, "pattern");
        var after = Text(proposed, "pattern");

        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return;
        }

        // Two regexes cannot be compared for subset in general, so any change is treated as a
        // narrowing and a removal as a widening. Over-reporting is the safe direction.
        if (after is not null)
        {
            found.Add(new Divergence(
                $"{path}/pattern",
                BreakingChangeKinds.StringConstraintNarrowed,
                CompatibilityDirection.Backward,
                CompatibilitySurface.WireJson,
                before is null
                    ? $"'pattern' added ({after}); documents written under the previous " +
                      "version may now be rejected."
                    : $"'pattern' changed from {before} to {after}; two patterns cannot be " +
                      "proven equivalent, so this is reported as a narrowing."));
        }
        else
        {
            found.Add(new Divergence(
                $"{path}/pattern",
                BreakingChangeKinds.StringConstraintWidened,
                CompatibilityDirection.Forward,
                CompatibilitySurface.WireJson,
                $"'pattern' removed ({before}); readers on the previous version may reject " +
                "documents written under this one."));
        }
    }

    private static void CompareAdditionalProperties(
        JsonElement previous, JsonElement proposed, string path, List<Divergence> found)
    {
        var before = Boolean(previous, "additionalProperties");
        var after = Boolean(proposed, "additionalProperties");

        if (before == after)
        {
            return;
        }

        if (before is not false && after is false)
        {
            found.Add(new Divergence(
                $"{path}/additionalProperties",
                BreakingChangeKinds.AdditionalPropertiesClosed,
                CompatibilityDirection.Backward,
                CompatibilitySurface.WireJson,
                "'additionalProperties' closed; documents written under the previous version " +
                "carrying undescribed properties will be rejected."));
        }
        else if (before is false && after is not false)
        {
            found.Add(new Divergence(
                $"{path}/additionalProperties",
                BreakingChangeKinds.AdditionalPropertiesOpened,
                CompatibilityDirection.Forward,
                CompatibilitySurface.WireJson,
                "'additionalProperties' opened; readers on the previous version will reject " +
                "documents carrying undescribed properties."));
        }
    }

    private static void CompareFormat(
        JsonElement previous, JsonElement proposed, string path, List<Divergence> found)
    {
        var before = Text(previous, "format");
        var after = Text(proposed, "format");

        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return;
        }

        // 'format' is an annotation by default in JSON Schema, so validation is unaffected -
        // but code generators map it to a type (date-time to DateTimeOffset, uuid to Guid).
        found.Add(new Divergence(
            $"{path}/format",
            BreakingChangeKinds.FormatChanged,
            CompatibilityDirection.Backward,
            CompatibilitySurface.Source,
            $"'format' changed from {Show(before)} to {Show(after)}. Validation is unaffected " +
            "because format is an annotation, but generated code changes type."));
    }

    // ----------------------------------------------------------------- utils

    private static Dictionary<string, JsonElement> Properties(JsonElement schema)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (schema.TryGetProperty("properties", out var properties) &&
            properties.ValueKind is JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                map[property.Name] = property.Value;
            }
        }

        return map;
    }

    private static HashSet<string> StringArray(JsonElement schema, string keyword)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty(keyword, out var array) && array.ValueKind is JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind is JsonValueKind.String)
                {
                    set.Add(item.GetString()!);
                }
            }
        }

        return set;
    }

    private static decimal? Number(JsonElement schema, string keyword) =>
        schema.TryGetProperty(keyword, out var value) &&
        value.ValueKind is JsonValueKind.Number &&
        value.TryGetDecimal(out var number)
            ? number
            : null;

    private static string? Text(JsonElement schema, string keyword) =>
        schema.TryGetProperty(keyword, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? Boolean(JsonElement schema, string keyword) =>
        schema.TryGetProperty(keyword, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    private static string Quote(IEnumerable<string> values) =>
        string.Join(", ", values.Order(StringComparer.Ordinal).Select(v => $"'{v}'"));

    private static string Show(decimal? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "absent";

    private static string Show(string? value) => value is null ? "absent" : $"'{value}'";

    // RFC 6901: '~' becomes '~0' and '/' becomes '~1'.
    private static string Escape(string token) =>
        token.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);
}
