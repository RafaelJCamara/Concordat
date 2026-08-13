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
    /// Nothing else is rewritten. In particular a generic type name keeps its backtick and
    /// brackets, so it fails validation rather than being mangled — see
    /// <see cref="Concordat.Domain.Messaging.MessageTypeSubjectResolver"/>.
    /// </para>
    /// </remarks>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.AsSpan().Trim();

        var comma = text.IndexOf(',');
        if (comma >= 0)
        {
            text = text[..comma].TrimEnd();
        }

        return text.ToString().Replace('+', '.').Replace(':', '.');
    }
}
