namespace Concordat.Domain.Registry;

/// <summary>
/// Reads a domain enumeration from the text a caller sent, and refuses what is not a member.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <c>Enum.TryParse</c> alone is not enough.</b> It succeeds for any integer, defined or
/// not: <c>"99"</c> parses to a <see cref="CompatibilityMode"/> that is no member. The value was then
/// stored, because nothing between the request and the database looked at it, and the failure
/// arrived *later*, in whatever projected it to its wire token — <c>WireTokens.For</c> throws on a
/// value it does not know. So the create answered 500 after it had committed, and every
/// <c>GET …/subjects</c> in that environment then answered 500 too, for every caller, until
/// somebody changed the stored value through the API. One request from anyone able to write took
/// the whole list down, and nothing in the product could say why.
/// </para>
/// <para>
/// A number that *is* a member is still read as that member (<c>"2"</c> is
/// <see cref="CompatibilityMode.Backward"/>), as it always was: narrowing what existing callers can
/// send is a separate decision from refusing what no member can be.
/// </para>
/// <para>
/// A comma is refused for a related reason. <c>Enum.TryParse</c> reads <c>"Backward,Forward"</c> as the
/// bitwise OR of two members even when the enumeration is not <c>[Flags]</c>, and here that is 6, which
/// happens to be <see cref="CompatibilityMode.Full"/>: a request that names two things is read as a
/// third without a word. Every OR of these members lands on a defined one, so it never corrupted
/// anything; it is refused because none of them is a set.
/// </para>
/// </remarks>
public static class WireEnums
{
    /// <summary>Parses one member by name or number, ignoring case, and refuses anything undefined.</summary>
    /// <typeparam name="TEnum">The enumeration.</typeparam>
    /// <param name="text">What the caller sent, already stripped of any separators the wire allows.</param>
    /// <param name="value">The member, when the text names one.</param>
    /// <returns><see langword="true"/> only for a defined member.</returns>
    public static bool TryParseDefined<TEnum>(string? text, out TEnum value)
        where TEnum : struct, Enum =>
        TryParseOne(text, out value) && Enum.IsDefined(value);

    private static bool TryParseOne<TEnum>(string? text, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;

        return text is not null &&
            !text.Contains(',', StringComparison.Ordinal) &&
            Enum.TryParse(text, ignoreCase: true, out value);
    }
}
