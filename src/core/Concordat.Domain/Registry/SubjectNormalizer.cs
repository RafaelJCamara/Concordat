namespace Concordat.Domain.Registry;

/// <summary>
/// Turns a publisher's type name into canonical subject text (ADR-011, DESIGN §3).
/// </summary>
/// <remarks>
/// <para>
/// Client-side only. The registry never calls this: it sees a string matching the subject
/// grammar and must not be able to tell which language produced it (ADR-019). The rules live
/// in the Domain beside <see cref="SubjectName"/>, alongside the envelope codec, because they
/// are pure text transformations that the conformance corpus pins for every SDK.
/// </para>
/// <para>
/// <b>Normalising at all is a risk, and it is taken deliberately.</b> Every rule here is a
/// rule that four other SDKs must reproduce exactly, or the same logical message type becomes
/// two subjects. That is why the set is small, mechanical and corpus-pinned rather than
/// generous — a lenient normaliser that guesses is the fastest route to a cross-language
/// split.
/// </para>
/// </remarks>
public static class SubjectNormalizer
{
    /// <summary>
    /// Applies the canonical rewrites. Does not validate; pass the result to
    /// <see cref="SubjectName.Create(string?)"/>.
    /// </summary>
    /// <param name="value">A type name as the publisher wrote it.</param>
    /// <returns>The rewritten text, which may still be invalid.</returns>
    /// <remarks>
    /// <para>
    /// Two rules, in order:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <description>
    /// <b>Everything from the first comma is dropped.</b> DESIGN §3 enumerates assembly,
    /// version, culture and public-key-token; all four live after that comma in a CLR
    /// assembly-qualified name, so one rule covers the list and there is nothing for another
    /// SDK to get subtly wrong. A namespace or type name cannot contain an unescaped comma,
    /// so nothing legitimate is lost.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <b><c>+</c> and <c>:</c> become <c>.</c></b> — the nested-type separator in .NET and
    /// the scope separator several brokers use.
    /// </description>
    /// </item>
    /// </list>
    /// <para>
    /// <b>Case is preserved.</b> <c>Acme.Orders</c> and <c>acme.orders</c> are different
    /// subjects, matching the ordinal, case-sensitive treatment of every other wire value in
    /// the protocol. Folding case would be a second lossy rewrite to keep four SDKs agreeing
    /// on, and it would mangle names that are meant to be read.
    /// </para>
    /// <para>
    /// <b>A closed generic type is spelled <c>Outer_of_Arg</c></b>, with <c>_and_</c> between
    /// further arguments (ADR-025, decision 10). <c>Acme.Envelope&lt;Acme.OrderCreated&gt;</c>
    /// becomes <c>Acme.Envelope_of_Acme.OrderCreated</c>.
    /// </para>
    /// <para>
    /// <b>The spelling is normative, not derived from CLR syntax</b>, and that distinction is
    /// the whole point. It is defined over the outer type's name and the argument names in
    /// order — which every language with generics can produce — rather than over
    /// <c>List`1[[Acme.Order, Asm, Version=…]]</c>, which only .NET can. A Go SDK reading
    /// <c>Envelope[OrderCreated]</c> and a Python SDK reading <c>Envelope[OrderCreated]</c>
    /// arrive at the same string, so the same logical contract is one subject in every language.
    /// Deriving each SDK's own spelling would have produced a different subject per language
    /// and a silent interop break, which is worse than refusing generics outright.
    /// </para>
    /// <para>
    /// Arity needs no marker: <c>X&lt;Y&lt;Z&gt;&gt;</c> is <c>X_of_Y_of_Z</c> and
    /// <c>X&lt;Y, Z&gt;</c> is <c>X_of_Y_and_Z</c>. A type literally named
    /// <c>Envelope_of_Order</c> would collide, which is the same rare, visible trade
    /// decision 11 accepted for nested types.
    /// </para>
    /// <para>
    /// Nothing else is rewritten.
    /// </para>
    /// </remarks>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();

        // Generics first, because the comma rule would otherwise cut the type arguments off at
        // the first assembly qualifier inside the brackets.
        if (TryNormalizeGeneric(trimmed, out var generic))
        {
            return generic;
        }

        var text = trimmed.AsSpan();

        var comma = text.IndexOf(',');
        if (comma >= 0)
        {
            text = text[..comma].TrimEnd();
        }

        return text.ToString().Replace('+', '.').Replace(':', '.');
    }

    /// <summary>Rewrites a CLR closed generic name to the normative spelling.</summary>
    /// <returns><see langword="false"/> when the text is not a generic name at all.</returns>
    private static bool TryNormalizeGeneric(string value, out string normalized)
    {
        normalized = string.Empty;

        var tick = value.IndexOf('`', StringComparison.Ordinal);
        var open = value.IndexOf('[', StringComparison.Ordinal);

        if (tick < 0 || open < tick)
        {
            return false;
        }

        var close = MatchingBracket(value, open);
        if (close < 0)
        {
            return false;
        }

        var outer = Normalize(value[..tick]);
        var arguments = SplitTopLevel(value[(open + 1)..close]);

        if (arguments.Count is 0)
        {
            return false;
        }

        var builder = new System.Text.StringBuilder(outer);

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i].Trim();

            // An assembly-qualified argument arrives wrapped in its own brackets. Unwrapping
            // before recursing is what lets the comma rule inside strip the qualifier.
            if (argument.Length > 1 && argument[0] is '[' && argument[^1] is ']')
            {
                argument = argument[1..^1];
            }

            builder.Append(i is 0 ? "_of_" : "_and_").Append(Normalize(argument));
        }

        normalized = builder.ToString();
        return true;
    }

    /// <summary>Finds the bracket closing the one at <paramref name="open"/>.</summary>
    private static int MatchingBracket(string value, int open)
    {
        var depth = 0;

        for (var i = open; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '[':
                    depth++;
                    break;

                case ']' when --depth is 0:
                    return i;
            }
        }

        return -1;
    }

    /// <summary>Splits on commas that are not inside a nested bracket group.</summary>
    private static List<string> SplitTopLevel(string value)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '[':
                    depth++;
                    break;

                case ']':
                    depth--;
                    break;

                case ',' when depth is 0:
                    parts.Add(value[start..i]);
                    start = i + 1;
                    break;
            }
        }

        if (start < value.Length)
        {
            parts.Add(value[start..]);
        }

        return parts;
    }
}
