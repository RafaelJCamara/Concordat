using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Concordat.Contracts.Generators;

/// <summary>
/// Compares two schema documents by structure and names the first place they differ.
/// </summary>
/// <remarks>
/// <para>
/// <b>Structural, not textual.</b> Comparing bytes would make the generator and the CLI's
/// canonicaliser two implementations of one format that must agree exactly — the divergence
/// this project spends most of its effort preventing. Comparing parsed shapes means whitespace,
/// key order and equivalent number spellings cannot cause a false failure, and only a real
/// change reports drift.
/// </para>
/// <para>
/// The parser is hand-written and dependency-free on purpose. An analyzer ships into the
/// compiler's own load context, where dragging in a JSON library invites version conflicts with
/// whatever the host already loaded — a failure the consumer sees as "the analyzer crashed",
/// with no way to act on it.
/// </para>
/// </remarks>
internal static class JsonComparer
{
    /// <summary>Finds the first structural difference.</summary>
    /// <param name="checkedIn">The contract file on disk.</param>
    /// <param name="generated">The schema built from the type.</param>
    /// <returns>A sentence naming the difference, or null when the two agree.</returns>
    public static string? FirstDifference(string checkedIn, string generated)
    {
        JsonValue left;
        JsonValue right;

        try
        {
            left = JsonValue.Parse(checkedIn);
            right = JsonValue.Parse(generated);
        }
        catch (FormatException ex)
        {
            return $"The checked-in file could not be parsed: {ex.Message}";
        }

        return Compare(left, right, "#");
    }

    private static string? Compare(JsonValue checkedIn, JsonValue generated, string path)
    {
        if (checkedIn.Kind != generated.Kind)
        {
            // The values, not the kind names. "the file has Array where the type produces
            // String" is technically accurate and tells a developer nothing;
            // ["string","null"] versus "string" tells them they removed a `?`.
            return $"At {path}: the file has {checkedIn.Render()}, the type produces {generated.Render()}.";
        }

        switch (checkedIn.Kind)
        {
            case JsonKind.Object:
                foreach (var key in generated.Members!.Keys
                             .Union(checkedIn.Members!.Keys, StringComparer.Ordinal)
                             .OrderBy(k => k, StringComparer.Ordinal))
                {
                    var hasLeft = checkedIn.Members.TryGetValue(key, out var l);
                    var hasRight = generated.Members.TryGetValue(key, out var r);

                    if (!hasLeft)
                    {
                        return $"At {path}: the type produces '{key}', which the file does not have.";
                    }

                    if (!hasRight)
                    {
                        return $"At {path}: the file has '{key}', which the type no longer produces.";
                    }

                    // `required` is a set by specification, so its order is not a difference.
                    var difference = string.Equals(key, "required", StringComparison.Ordinal)
                        ? CompareStringSet(l!, r!, $"{path}/{key}")
                        : Compare(l!, r!, $"{path}/{key}");

                    if (difference is not null)
                    {
                        return difference;
                    }
                }

                return null;

            case JsonKind.Array:
                if (checkedIn.Items!.Count != generated.Items!.Count)
                {
                    return $"At {path}: the file has {checkedIn.Items.Count} item(s), " +
                           $"the type produces {generated.Items.Count}.";
                }

                for (var i = 0; i < checkedIn.Items.Count; i++)
                {
                    var difference = Compare(checkedIn.Items[i], generated.Items[i], $"{path}/{i}");
                    if (difference is not null)
                    {
                        return difference;
                    }
                }

                return null;

            case JsonKind.String:
                return string.Equals(checkedIn.Text, generated.Text, StringComparison.Ordinal)
                    ? null
                    : $"At {path}: the file says \"{checkedIn.Text}\", the type produces \"{generated.Text}\".";

            case JsonKind.Number:
                return checkedIn.Number.Equals(generated.Number)
                    ? null
                    : $"At {path}: the file says {checkedIn.Text}, the type produces {generated.Text}.";

            default:
                return null;
        }
    }

    private static string? CompareStringSet(JsonValue checkedIn, JsonValue generated, string path)
    {
        if (checkedIn.Kind is not JsonKind.Array || generated.Kind is not JsonKind.Array)
        {
            return Compare(checkedIn, generated, path);
        }

        var left = checkedIn.Items!.Select(i => i.Text ?? string.Empty).ToList();
        var right = generated.Items!.Select(i => i.Text ?? string.Empty).ToList();

        var added = right.Except(left, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var removed = left.Except(right, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();

        if (added.Count == 0 && removed.Count == 0)
        {
            return null;
        }

        var message = new StringBuilder($"At {path}:");

        if (added.Count > 0)
        {
            message.Append(" now required: ").Append(string.Join(", ", added)).Append('.');
        }

        if (removed.Count > 0)
        {
            message.Append(" no longer required: ").Append(string.Join(", ", removed)).Append('.');
        }

        return message.ToString();
    }

    private enum JsonKind
    {
        Object,
        Array,
        String,
        Number,
        Boolean,
        Null,
    }

    /// <summary>Just enough JSON to compare two schema documents.</summary>
    private sealed class JsonValue
    {
        public JsonKind Kind { get; private set; }

        public Dictionary<string, JsonValue>? Members { get; private set; }

        public List<JsonValue>? Items { get; private set; }

        public string? Text { get; private set; }

        public double Number { get; private set; }

        /// <summary>A compact rendering, truncated so a diagnostic stays one readable line.</summary>
        public string Render()
        {
            switch (Kind)
            {
                case JsonKind.String: return $"\"{Text}\"";
                case JsonKind.Number or JsonKind.Boolean or JsonKind.Null: return Text ?? "null";

                case JsonKind.Array:
                    var elements = Items ?? [];
                    var items = string.Join(",", elements.Take(6).Select(i => i.Render()));
                    return elements.Count > 6 ? $"[{items},…]" : $"[{items}]";

                default:
                    var members = Members ?? [];
                    var keys = string.Join(", ", members.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(6));
                    return members.Count > 6 ? $"an object with {keys}, …" : $"an object with {keys}";
            }
        }

        public static JsonValue Parse(string text)
        {
            var index = 0;
            var value = Read(text, ref index);
            SkipWhitespace(text, ref index);

            return index == text.Length
                ? value
                : throw new FormatException($"unexpected content at position {index}.");
        }

        private static JsonValue Read(string text, ref int index)
        {
            SkipWhitespace(text, ref index);

            if (index >= text.Length)
            {
                throw new FormatException("unexpected end of input.");
            }

            switch (text[index])
            {
                case '{': return ReadObject(text, ref index);
                case '[': return ReadArray(text, ref index);
                case '"': return new JsonValue { Kind = JsonKind.String, Text = ReadString(text, ref index) };

                case 't':
                    Expect(text, ref index, "true");
                    return new JsonValue { Kind = JsonKind.Boolean, Text = "true" };

                case 'f':
                    Expect(text, ref index, "false");
                    return new JsonValue { Kind = JsonKind.Boolean, Text = "false" };

                case 'n':
                    Expect(text, ref index, "null");
                    return new JsonValue { Kind = JsonKind.Null, Text = "null" };

                default: return ReadNumber(text, ref index);
            }
        }

        private static JsonValue ReadObject(string text, ref int index)
        {
            var members = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            index++;
            SkipWhitespace(text, ref index);

            if (index < text.Length && text[index] == '}')
            {
                index++;
                return new JsonValue { Kind = JsonKind.Object, Members = members };
            }

            while (true)
            {
                SkipWhitespace(text, ref index);
                var name = ReadString(text, ref index);
                SkipWhitespace(text, ref index);

                if (index >= text.Length || text[index] != ':')
                {
                    throw new FormatException($"expected ':' at position {index}.");
                }

                index++;
                members[name] = Read(text, ref index);
                SkipWhitespace(text, ref index);

                if (index >= text.Length)
                {
                    throw new FormatException("unterminated object.");
                }

                if (text[index] == ',')
                {
                    index++;
                    continue;
                }

                if (text[index] == '}')
                {
                    index++;
                    return new JsonValue { Kind = JsonKind.Object, Members = members };
                }

                throw new FormatException($"expected ',' or '}}' at position {index}.");
            }
        }

        private static JsonValue ReadArray(string text, ref int index)
        {
            var items = new List<JsonValue>();
            index++;
            SkipWhitespace(text, ref index);

            if (index < text.Length && text[index] == ']')
            {
                index++;
                return new JsonValue { Kind = JsonKind.Array, Items = items };
            }

            while (true)
            {
                items.Add(Read(text, ref index));
                SkipWhitespace(text, ref index);

                if (index >= text.Length)
                {
                    throw new FormatException("unterminated array.");
                }

                if (text[index] == ',')
                {
                    index++;
                    continue;
                }

                if (text[index] == ']')
                {
                    index++;
                    return new JsonValue { Kind = JsonKind.Array, Items = items };
                }

                throw new FormatException($"expected ',' or ']' at position {index}.");
            }
        }

        private static string ReadString(string text, ref int index)
        {
            if (index >= text.Length || text[index] != '"')
            {
                throw new FormatException($"expected a string at position {index}.");
            }

            index++;
            var value = new StringBuilder();

            while (index < text.Length)
            {
                var c = text[index++];

                if (c == '"')
                {
                    return value.ToString();
                }

                if (c != '\\')
                {
                    value.Append(c);
                    continue;
                }

                if (index >= text.Length)
                {
                    break;
                }

                var escape = text[index++];
                switch (escape)
                {
                    case '"': value.Append('"'); break;
                    case '\\': value.Append('\\'); break;
                    case '/': value.Append('/'); break;
                    case 'b': value.Append('\b'); break;
                    case 'f': value.Append('\f'); break;
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;

                    case 'u':
                        if (index + 4 > text.Length)
                        {
                            throw new FormatException("truncated \\u escape.");
                        }

                        value.Append((char)Convert.ToInt32(text.Substring(index, 4), 16));
                        index += 4;
                        break;

                    default: throw new FormatException($"unknown escape '\\{escape}'.");
                }
            }

            throw new FormatException("unterminated string.");
        }

        private static JsonValue ReadNumber(string text, ref int index)
        {
            var start = index;

            while (index < text.Length && "+-.eE0123456789".IndexOf(text[index]) >= 0)
            {
                index++;
            }

            var literal = text.Substring(start, index - start);

            return double.TryParse(
                literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? new JsonValue { Kind = JsonKind.Number, Number = number, Text = literal }
                : throw new FormatException($"'{literal}' is not a number.");
        }

        private static void Expect(string text, ref int index, string literal)
        {
            if (index + literal.Length > text.Length
                || string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
            {
                throw new FormatException($"expected '{literal}' at position {index}.");
            }

            index += literal.Length;
        }

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }
        }
    }
}
