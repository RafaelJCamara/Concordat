using System.Text.Json;
using Concordat.Domain.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Concordat.Infrastructure.Persistence;

/// <summary>
/// Maps a list of <see cref="ActorId"/> to one text column holding a JSON array of strings.
/// </summary>
/// <remarks>
/// <para>
/// <b>JSON, where <see cref="SubjectRefColumn"/> joins with commas.</b> A subject name cannot contain a
/// comma under its own grammar, so a comma was a safe separator there. An actor id can: it is any
/// non-blank text up to 256 characters, and an API key's label is whatever its issuer typed. A
/// separator would have turned <c>ci, prod</c> into two reviewers, and the failure would have been
/// a rule that names someone nobody asked for. A JSON array has no such character.
/// </para>
/// <para>
/// One column and not a child table, for the reason the subject list is: nothing queries an entry
/// on its own, it has no identity, and it is read and written whole with its parent. The comparer
/// is not optional (see <see cref="SubjectRefColumn"/>).
/// </para>
/// </remarks>
internal static class ActorListColumn
{
    /// <summary>The widest a serialised list may be, in characters.</summary>
    /// <remarks>
    /// Twenty actors at the longest permitted length is about 5 KB of text once quoted; the column
    /// is not narrower than that, and the domain's own limit is what bounds the set.
    /// </remarks>
    public const int MaxLength = 8192;

    /// <summary>Configures a property as a serialised actor list.</summary>
    /// <param name="property">The property builder.</param>
    /// <param name="columnName">The column name.</param>
    public static void Configure(PropertyBuilder<IReadOnlyList<ActorId>> property, string columnName)
    {
        ArgumentNullException.ThrowIfNull(property);

        property
            .HasColumnName(columnName)
            .HasMaxLength(MaxLength)
            .IsRequired()
            .HasConversion(
                actors => Serialise(actors),
                text => Deserialise(text),
                new ValueComparer<IReadOnlyList<ActorId>>(
                    (left, right) => Serialise(left!) == Serialise(right!),
                    actors => Serialise(actors).GetHashCode(StringComparison.Ordinal),
                    actors => Deserialise(Serialise(actors))));
    }

    /// <summary>Renders an actor list.</summary>
    /// <param name="actors">The actors.</param>
    /// <returns>The stored text.</returns>
    public static string Serialise(IReadOnlyList<ActorId> actors)
    {
        ArgumentNullException.ThrowIfNull(actors);

        return JsonSerializer.Serialize(actors.Select(a => a.Value).ToArray());
    }

    /// <summary>Reads an actor list back.</summary>
    /// <param name="text">The stored text.</param>
    /// <returns>The actors.</returns>
    public static IReadOnlyList<ActorId> Deserialise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // An empty column reads as an empty list rather than throwing: a row written by hand, or
        // by a migration that forgot its default, must not make a subject unreadable.
        var values = text.Length == 0 ? [] : JsonSerializer.Deserialize<string[]>(text) ?? [];

        return [.. values.Select(v => ActorId.Create(v).Value)];
    }
}
