using Concordat.Domain.Contracts;
using Concordat.Domain.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Concordat.Infrastructure.Persistence;

/// <summary>
/// Maps a list of <see cref="SubjectRef"/> to one text column.
/// </summary>
/// <remarks>
/// <para>
/// <b>One column, not a child table.</b> These are value objects owned entirely by their
/// parent: nothing queries them independently, they have no identity, and they are always read
/// and written together. A child table would add a join and two shadow keys to model what is
/// conceptually one field. The format is <c>subject@selector</c> joined by commas, which is
/// unambiguous because a subject name cannot contain <c>@</c> or <c>,</c> under its own
/// grammar, and readable in a database client — which matters more than it sounds for a table
/// operators inspect when a contract behaves unexpectedly.
/// </para>
/// <para>
/// Shared by contracts (M7.3) and service registrations (M7.4) rather than written twice. Two
/// copies of a format this quiet would drift, and the failure would be a binding that reads
/// back with different subjects than it was saved with.
/// </para>
/// </remarks>
internal static class SubjectRefColumn
{
    /// <summary>The widest a serialised subject list may be, in characters.</summary>
    public const int MaxLength = 4096;

    /// <summary>Configures a property as a serialised subject list.</summary>
    /// <param name="property">The property builder.</param>
    /// <param name="columnName">The column name.</param>
    /// <remarks>
    /// The comparer is not optional. Without it EF compares the converted collection by
    /// reference, so a parent whose subjects changed is silently considered unmodified and
    /// never written — a bug that only appears on the second save.
    /// </remarks>
    public static void Configure(
        PropertyBuilder<IReadOnlyList<SubjectRef>> property, string columnName = "subjects")
    {
        ArgumentNullException.ThrowIfNull(property);

        property
            .HasColumnName(columnName)
            .HasMaxLength(MaxLength)
            .IsRequired()
            .HasConversion(
                refs => Serialise(refs),
                text => Deserialise(text),
                new ValueComparer<IReadOnlyList<SubjectRef>>(
                    (left, right) => Serialise(left!) == Serialise(right!),
                    refs => Serialise(refs).GetHashCode(StringComparison.Ordinal),
                    refs => Deserialise(Serialise(refs))));
    }

    /// <summary>Renders a subject list.</summary>
    /// <param name="refs">The references.</param>
    /// <returns>The stored text.</returns>
    public static string Serialise(IReadOnlyList<SubjectRef> refs)
    {
        ArgumentNullException.ThrowIfNull(refs);

        return string.Join(',', refs.Select(r => $"{r.Subject.Value}@{r.Selector}"));
    }

    /// <summary>Reads a subject list back.</summary>
    /// <param name="text">The stored text.</param>
    /// <returns>The references.</returns>
    public static IReadOnlyList<SubjectRef> Deserialise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Length is 0
            ? []
            : [.. text.Split(',').Select(entry =>
            {
                var at = entry.LastIndexOf('@');
                return new SubjectRef(
                    SubjectName.Create(entry[..at]).Value,
                    VersionSelector.Parse(entry[(at + 1)..]).Value);
            })];
    }
}
