using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Concordat.Domain.Results;

namespace Concordat.Formats.Abstractions;

/// <summary>
/// Finds a UTF-16 surrogate escape with no partner in JSON text, before anything reads the
/// string it would decode to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a refusal and not a repair.</b> The JSON grammar allows any four-hex-digit
/// escape, so <c>"\ud800"</c> is well-formed and <see cref="JsonDocument"/> accepts it. What
/// cannot exist is the string it stands for: a surrogate is only a character as the high half
/// followed by the low half of a pair, and alone it has no UTF-8 spelling. Parsers disagree on
/// what to do, measured: Go replaces it with U+FFFD while parsing; JavaScript and Python keep it as
/// a string they cannot then encode (JavaScript writes U+FFFD, Python raises); and .NET's reader
/// throws when the string is read. The same bytes would mean different things to a producer and a
/// consumer. That is the argument that refuses a duplicate object key,
/// and RFC 7493 (I-JSON) refuses these for the same reason; RFC 8259 §8.2 only warns that the
/// behaviour is unpredictable. A canonical form is UTF-8 text that is hashed, so there is no way
/// to canonicalise one faithfully either.
/// </para>
/// <para>
/// <b>Why this scans the raw text instead of asking the document.</b> The failure is not in the
/// writer, as it first looked: <c>JsonElement.GetString()</c> and <c>JsonProperty.Name</c> throw
/// <see cref="InvalidOperationException"/> on such a string, so the first walk of the tree —
/// including the duplicate-key check — dies. Reading the escapes as written never decodes the
/// string, so it cannot throw, and it reports a document position rather than a stack trace.
/// </para>
/// <para>
/// <b>What the message may not contain.</b> The offending string itself. A Problem Details body
/// carrying an unpaired surrogate fails to serialise, which turns the refusal back into the 500
/// it replaces; the duplicate-key message quotes its key, so this check must run before that one.
/// The unit is reported as the escape that was written, and a key that holds one is shown as it
/// was written too.
/// </para>
/// </remarks>
public static class UnpairedSurrogates
{
    /// <summary>The longest location the message will quote, in UTF-16 code units.</summary>
    private const int MaxLocationLength = 200;

    private const byte Backslash = (byte)'\\';

    /// <summary>Looks for a surrogate escape with no partner in a document that already parsed.</summary>
    /// <param name="utf8Json">
    /// The UTF-8 text of a document <see cref="JsonDocument"/> has accepted. Passing one it has
    /// not is a caller's error: the reader would throw <see cref="JsonException"/> on it.
    /// </param>
    /// <returns>
    /// A <see cref="ConcordatCodes.SchemaMalformed"/> error naming the JSON Pointer of the first
    /// string, in document order, that holds one; or <see langword="null"/>.
    /// </returns>
    public static DomainError? Find(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json);

        // One frame per open container. An array frame counts its elements; an object frame
        // remembers the key whose value is being read, already escaped as a JSON Pointer segment.
        var frames = new List<Frame>();

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    if (reader.ValueIsEscaped &&
                        FindUnit(reader.ValueSpan) is var keyUnit and >= 0)
                    {
                        // As written: decoding this key is the thing that throws.
                        var written = Encoding.UTF8.GetString(reader.ValueSpan);
                        return Refusal(Pointer(frames, EscapePointer(written)), keyUnit);
                    }

                    frames[^1] = frames[^1] with { Key = EscapePointer(reader.GetString()!) };
                    break;

                case JsonTokenType.String:
                    var position = Advance(frames);
                    if (reader.ValueIsEscaped && FindUnit(reader.ValueSpan) is var unit and >= 0)
                    {
                        return Refusal(Pointer(frames, position), unit);
                    }

                    break;

                case JsonTokenType.StartObject:
                    Advance(frames);
                    frames.Add(new Frame(IsArray: false, Count: 0, Key: null));
                    break;

                case JsonTokenType.StartArray:
                    Advance(frames);
                    frames.Add(new Frame(IsArray: true, Count: 0, Key: null));
                    break;

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    frames.RemoveAt(frames.Count - 1);
                    break;

                default:
                    // Numbers, booleans and null carry no string, but they still take a position
                    // in an array that a later string's pointer has to count.
                    Advance(frames);
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// The first unpaired surrogate escape in one string's raw text, as the code unit, or -1.
    /// </summary>
    private static int FindUnit(ReadOnlySpan<byte> raw)
    {
        var i = 0;
        while (i < raw.Length)
        {
            if (raw[i] != Backslash)
            {
                i++;
                continue;
            }

            // The reader has already refused a malformed escape, so there is a character after
            // every backslash. Anything but 'u' is a two-byte escape, including an escaped
            // backslash, whose second byte must not be mistaken for the start of another.
            if (raw[i + 1] != (byte)'u')
            {
                i += 2;
                continue;
            }

            var unit = HexUnit(raw.Slice(i + 2, 4));
            i += 6;

            if (char.IsLowSurrogate((char)unit))
            {
                return unit;
            }

            if (!char.IsHighSurrogate((char)unit))
            {
                continue;
            }

            // A high half is only acceptable when the very next thing is a low half, written as
            // an escape too: a raw low half cannot follow, because it has no UTF-8 spelling.
            if (i + 6 <= raw.Length &&
                raw[i] == Backslash &&
                raw[i + 1] == (byte)'u' &&
                char.IsLowSurrogate((char)HexUnit(raw.Slice(i + 2, 4))))
            {
                i += 6;
                continue;
            }

            return unit;
        }

        return -1;
    }

    private static int HexUnit(ReadOnlySpan<byte> digits) =>
        // The reader has already checked these are four hex digits, in either case.
        Utf8Parser.TryParse(digits, out int unit, out _, 'X') ? unit : -1;

    private static DomainError Refusal(string location, int unit)
    {
        var shown = location.Length == 0 ? "the document itself" : $"'{Truncate(location)}'";
        var escape = $"{(char)Backslash}u{unit.ToString("X4", CultureInfo.InvariantCulture)}";

        return new DomainError(
            ConcordatCodes.SchemaMalformed,
            $"The string at {shown} holds the UTF-16 surrogate escape {escape} with no partner. " +
            "A surrogate is only a character as the high half followed by the low half of one " +
            "pair; alone the string has no UTF-8 spelling, and parsers disagree about what it " +
            "is (keep it, replace it with U+FFFD, or refuse the document), so this document " +
            "has no single meaning and cannot be given an id.");
    }

    /// <summary>
    /// Bounds the quoted location without splitting a surrogate pair, which would put an
    /// unpaired one into the message and fail the response the way the document did.
    /// </summary>
    private static string Truncate(string location)
    {
        if (location.Length <= MaxLocationLength)
        {
            return location;
        }

        var cut = char.IsHighSurrogate(location[MaxLocationLength - 1])
            ? MaxLocationLength - 1
            : MaxLocationLength;

        return string.Concat(location.AsSpan(0, cut), "…");
    }

    /// <summary>Counts a value in its parent and returns its pointer segment, or null at the root.</summary>
    private static string? Advance(List<Frame> frames)
    {
        if (frames.Count == 0)
        {
            return null;
        }

        var parent = frames[^1];
        if (!parent.IsArray)
        {
            return parent.Key;
        }

        frames[^1] = parent with { Count = parent.Count + 1 };
        return parent.Count.ToString(CultureInfo.InvariantCulture);
    }

    private static string Pointer(List<Frame> frames, string? last)
    {
        // `frames` ends at the container the value is in; every frame above it is a container
        // whose current element is the one on the way down.
        var builder = new StringBuilder();
        for (var i = 0; i < frames.Count - 1; i++)
        {
            builder.Append('/').Append(Segment(frames[i]));
        }

        if (last is not null)
        {
            builder.Append('/').Append(last);
        }

        return builder.ToString();
    }

    private static string Segment(Frame frame) =>
        frame.IsArray
            ? (frame.Count - 1).ToString(CultureInfo.InvariantCulture)
            : frame.Key ?? string.Empty;

    private static string EscapePointer(string name) =>
        name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private readonly record struct Frame(bool IsArray, int Count, string? Key);
}
