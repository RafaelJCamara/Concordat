using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;

namespace Concordat.Formats.Avro;

/// <summary>
/// Applies Avro's schema resolution rules to one (writer, reader) pair and reports every way
/// the reader fails to read what the writer produces.
/// </summary>
/// <remarks>
/// <para>
/// <b>One direction per call, because Avro compatibility is genuinely asymmetric.</b> Avro
/// binary carries no field names or tags — it is positional — so decoding is only defined
/// relative to a (writer, reader) pair, and the rules are not symmetric in that pair. Rather
/// than a "narrower/wider" heuristic, this implements resolution once and the checker calls it
/// twice with the roles swapped:
/// </para>
/// <list type="bullet">
///   <item><description><b>Backward</b> — the new schema reads old data, so
///     <c>writer = previous</c>, <c>reader = proposed</c>.</description></item>
///   <item><description><b>Forward</b> — the old schema reads new data, so
///     <c>writer = proposed</c>, <c>reader = previous</c>.</description></item>
/// </list>
/// <para>
/// <b>Surfaces are mostly <see cref="CompatibilitySurface.Wire"/>, and that is the opposite of
/// JSON Schema.</b> A JSON document is self-describing, so a divergence only fails validation.
/// An Avro resolution failure means the bytes do not decode at all. The two exceptions:
/// a numeric promotion decodes fine but changes the generated type
/// (<see cref="CompatibilitySurface.Source"/>), and an unknown enum symbol absorbed by the
/// reader's <c>default</c> decodes to a different value than was written
/// (<see cref="CompatibilitySurface.WireJson"/>).
/// </para>
/// <para>
/// <b>Paths are name-based, not RFC 6901 index-based.</b> Avro matches record fields by name,
/// so a name is the stable identifier for a field and an index is not — reordering fields is a
/// compatible change that would renumber every index-based path.
/// </para>
/// </remarks>
internal static class AvroSchemaResolver
{
    /// <summary>One way the reader fails to read the writer's output.</summary>
    /// <param name="Path">Where, as a name-based path into the schema.</param>
    /// <param name="Kind">A token from <see cref="BreakingChangeKinds"/>.</param>
    /// <param name="Surface">The narrowest surface this violates.</param>
    /// <param name="Message">What breaks, and what it costs.</param>
    internal sealed record Divergence(
        string Path,
        string Kind,
        CompatibilitySurface Surface,
        string Message);

    /// <summary>
    /// The specification's permitted type promotions: a reader of the wider type can read a
    /// narrower writer's output.
    /// </summary>
    /// <remarks>
    /// Strictly one-way. <c>int</c> promotes to <c>long</c>, but a <c>long</c> writer and an
    /// <c>int</c> reader is an error — which is exactly why the backward and forward runs
    /// produce different findings for the same edit.
    /// </remarks>
    private static readonly Dictionary<string, string[]> Promotions = new(StringComparer.Ordinal)
    {
        ["int"] = ["long", "float", "double"],
        ["long"] = ["float", "double"],
        ["float"] = ["double"],
        ["string"] = ["bytes"],
        ["bytes"] = ["string"],
    };

    /// <summary>Checks whether <paramref name="reader"/> can read <paramref name="writer"/>.</summary>
    /// <param name="writer">The document data was written under.</param>
    /// <param name="reader">The document doing the reading.</param>
    /// <param name="direction">Which compatibility direction this pairing represents.</param>
    /// <returns>Every resolution failure, in document order.</returns>
    public static List<Divergence> Check(
        AvroDocument writer, AvroDocument reader, CompatibilityDirection direction)
    {
        var context = new Context(writer, reader, direction, [], []);
        Compare(writer.Root, reader.Root, "#", context);
        return context.Found;
    }

    private sealed record Context(
        AvroDocument Writer,
        AvroDocument Reader,
        CompatibilityDirection Direction,
        List<Divergence> Found,
        HashSet<(string Writer, string Reader)> Visited);

    // ------------------------------------------------------------------ dispatch

    private static void Compare(
        AvroType writerType, AvroType readerType, string path, Context context)
    {
        var writer = context.Writer.Deref(writerType);
        var reader = context.Reader.Deref(readerType);

        // A writer union means data could have been written as any branch, so every branch has
        // to be readable. Checked before the reader-union case so a union-to-union comparison
        // decomposes into per-branch questions.
        if (writer is AvroUnion writerUnion)
        {
            foreach (var branch in writerUnion.Branches)
            {
                Compare(branch, reader, path, context);
            }

            return;
        }

        if (reader is AvroUnion readerUnion)
        {
            // The reader resolves a non-union writer against the first branch that matches.
            var match = readerUnion.Branches.FirstOrDefault(b => Matches(writer, b, context));
            if (match is null)
            {
                ReportTypeMismatch(writer, reader, path, context);
                return;
            }

            Compare(writer, match, path, context);
            return;
        }

        switch (writer, reader)
        {
            case (AvroPrimitive w, AvroPrimitive r):
                ComparePrimitives(w, r, path, context);
                return;

            case (AvroRecord w, AvroRecord r):
                CompareRecords(w, r, path, context);
                return;

            case (AvroEnum w, AvroEnum r):
                CompareEnums(w, r, path, context);
                return;

            case (AvroFixed w, AvroFixed r):
                CompareFixed(w, r, path, context);
                return;

            case (AvroArray w, AvroArray r):
                Compare(w.Items, r.Items, $"{path}/items", context);
                return;

            case (AvroMap w, AvroMap r):
                Compare(w.Values, r.Values, $"{path}/values", context);
                return;

            default:
                ReportTypeMismatch(writer, reader, path, context);
                return;
        }
    }

    // ---------------------------------------------------------------- primitives

    private static void ComparePrimitives(
        AvroPrimitive writer, AvroPrimitive reader, string path, Context context)
    {
        if (string.Equals(writer.Name, reader.Name, StringComparison.Ordinal))
        {
            return;
        }

        if (IsPromotable(writer.Name, reader.Name))
        {
            // The bytes decode: this is Avro's int32 -> int64, the case ADR-016's second axis
            // exists to express. Permitted under WireJson, blocked under Source.
            context.Found.Add(new Divergence(
                path,
                BreakingChangeKinds.TypePromoted,
                CompatibilitySurface.Source,
                $"'{writer.Name}' is promoted to '{reader.Name}' on read. Every existing " +
                "value still decodes, but generated code changes type."));
            return;
        }

        ReportTypeMismatch(writer, reader, path, context);
    }

    private static bool IsPromotable(string writer, string reader) =>
        Promotions.TryGetValue(writer, out var targets) &&
        targets.Contains(reader, StringComparer.Ordinal);

    // ------------------------------------------------------------------- records

    private static void CompareRecords(
        AvroRecord writer, AvroRecord reader, string path, Context context)
    {
        if (!NameMatches(writer.FullName, reader.FullName, reader.Aliases))
        {
            ReportNameChanged(writer.FullName, reader.FullName, path, "record", context);
            return;
        }

        // Avro permits a type to reference itself, so the walk has to be bounded. A pair seen
        // before yields nothing new: the comparison is structural and has no per-visit state.
        if (!context.Visited.Add((writer.FullName, reader.FullName)))
        {
            return;
        }

        foreach (var field in reader.Fields)
        {
            var counterpart = FindWriterField(writer, field);
            if (counterpart is not null)
            {
                Compare(counterpart.Type, field.Type, $"{path}/fields/{field.Name}", context);
                continue;
            }

            // The rule that makes Avro workable: a reader field the writer never wrote takes
            // its default, and is an error without one. This is why "add a field with a
            // default" is the ordinary, safe Avro change - and why the same edit without a
            // default is not.
            if (field.HasDefault)
            {
                continue;
            }

            context.Found.Add(new Divergence(
                $"{path}/fields/{field.Name}",
                context.Direction is CompatibilityDirection.Backward
                    ? BreakingChangeKinds.RequiredFieldAdded
                    : BreakingChangeKinds.RequiredFieldRemoved,
                CompatibilitySurface.Wire,
                context.Direction is CompatibilityDirection.Backward
                    ? $"Field '{field.Name}' was added with no default, so there is nothing to " +
                      "read it as in data written under the previous version. Give it a " +
                      "default to make this compatible."
                    : $"Field '{field.Name}' was removed, and the previous version declares no " +
                      "default for it, so readers still on that version cannot decode data " +
                      "written under this one."));
        }

        // A writer field the reader does not declare is skipped during decoding. That is
        // deliberate and produces no finding: it is Avro's equivalent of the optional-property
        // removal that DESIGN §7 requires to be fully compatible.
    }

    /// <summary>Finds the writer field a reader field resolves against, honouring aliases.</summary>
    /// <remarks>
    /// Aliases are consulted on the <em>reader's</em> field only, per the specification. That
    /// asymmetry is load-bearing: it means a rename is recoverable in the backward direction,
    /// where the proposal is the reader and can name what it used to be called, and not in the
    /// forward direction, where the reader is a schema written before the new name existed.
    /// </remarks>
    private static AvroField? FindWriterField(AvroRecord writer, AvroField readerField)
    {
        var direct = writer.Fields.FirstOrDefault(
            f => string.Equals(f.Name, readerField.Name, StringComparison.Ordinal));

        return direct ?? writer.Fields.FirstOrDefault(
            f => readerField.Aliases.Contains(f.Name, StringComparer.Ordinal));
    }

    // --------------------------------------------------------------------- enums

    private static void CompareEnums(
        AvroEnum writer, AvroEnum reader, string path, Context context)
    {
        if (!NameMatches(writer.FullName, reader.FullName, reader.Aliases))
        {
            ReportNameChanged(writer.FullName, reader.FullName, path, "enum", context);
            return;
        }

        var unknown = writer.Symbols
            .Where(s => !reader.Symbols.Contains(s, StringComparer.Ordinal))
            .ToList();

        if (unknown.Count == 0)
        {
            return;
        }

        var symbols = string.Join(", ", unknown.Select(s => $"'{s}'"));

        if (reader.Default is not null)
        {
            // Avro 1.9's enum default absorbs unknown symbols. The bytes decode, so this is not
            // a Wire break - but the value the application sees is not the value that was
            // written, which is precisely a broken JSON mapping.
            context.Found.Add(new Divergence(
                path,
                BreakingChangeKinds.EnumValueDefaulted,
                CompatibilitySurface.WireJson,
                $"Symbol(s) {symbols} are absent and resolve to the reader's default " +
                $"'{reader.Default}'. Decoding succeeds, but the value read is not the value " +
                "written."));
            return;
        }

        context.Found.Add(new Divergence(
            path,
            context.Direction is CompatibilityDirection.Backward
                ? BreakingChangeKinds.EnumValueRemoved
                : BreakingChangeKinds.EnumValueAdded,
            CompatibilitySurface.Wire,
            context.Direction is CompatibilityDirection.Backward
                ? $"Symbol(s) {symbols} were removed; data written under the previous version " +
                  "carrying them cannot be decoded. Declare an enum 'default' to absorb them."
                : $"Symbol(s) {symbols} were added; readers still on the previous version " +
                  "cannot decode data carrying them."));
    }

    // --------------------------------------------------------------------- fixed

    private static void CompareFixed(
        AvroFixed writer, AvroFixed reader, string path, Context context)
    {
        if (!NameMatches(writer.FullName, reader.FullName, reader.Aliases))
        {
            ReportNameChanged(writer.FullName, reader.FullName, path, "fixed", context);
            return;
        }

        if (writer.Size == reader.Size)
        {
            return;
        }

        // A fixed is length-prefixed by its schema, not by the data, so a size change
        // misaligns every byte that follows it.
        context.Found.Add(new Divergence(
            path,
            BreakingChangeKinds.FixedSizeChanged,
            CompatibilitySurface.Wire,
            $"'fixed' size changed from {writer.Size} to {reader.Size}. The width is part of " +
            "the type's identity, so nothing written under the other size can be decoded."));
    }

    // ------------------------------------------------------------------- reports

    private static void ReportNameChanged(
        string writerName, string readerName, string path, string kind, Context context) =>
        context.Found.Add(new Divergence(
            path,
            BreakingChangeKinds.NameChanged,
            CompatibilitySurface.Wire,
            $"The {kind} named '{writerName}' is now '{readerName}'. Avro resolves named types " +
            "by fullname, so this does not decode. " +
            (context.Direction is CompatibilityDirection.Backward
                ? $"Adding \"aliases\": [\"{writerName}\"] to the new type makes it compatible."
                : "Forward compatibility cannot be recovered by an alias, because aliases are " +
                  "declared on the reader and the previous version was written first.")));

    private static void ReportTypeMismatch(
        AvroType writer, AvroType reader, string path, Context context) =>
        context.Found.Add(new Divergence(
            path,
            context.Direction is CompatibilityDirection.Backward
                ? BreakingChangeKinds.TypeNarrowed
                : BreakingChangeKinds.TypeWidened,
            CompatibilitySurface.Wire,
            $"{Describe(writer)} cannot be read as {Describe(reader)}; Avro defines no " +
            "promotion between them, so the bytes do not decode."));

    // --------------------------------------------------------------------- utils

    private static bool NameMatches(
        string writerName, string readerName, IReadOnlyList<string> readerAliases) =>
        string.Equals(writerName, readerName, StringComparison.Ordinal) ||
        readerAliases.Contains(writerName, StringComparer.Ordinal);

    /// <summary>
    /// Whether a writer type could resolve against a reader union branch, used only to pick
    /// which branch to descend into.
    /// </summary>
    /// <remarks>
    /// Deliberately shallow. It answers "is this the branch the reader would choose", which is
    /// a naming and kind question; whether the branch then <em>fits</em> is the recursive
    /// comparison's job, and doing it here would report the same divergence twice.
    /// </remarks>
    private static bool Matches(AvroType writer, AvroType branch, Context context)
    {
        var target = context.Reader.Deref(branch);

        return (writer, target) switch
        {
            (AvroPrimitive w, AvroPrimitive r) =>
                string.Equals(w.Name, r.Name, StringComparison.Ordinal) ||
                IsPromotable(w.Name, r.Name),
            (AvroRecord w, AvroRecord r) => NameMatches(w.FullName, r.FullName, r.Aliases),
            (AvroEnum w, AvroEnum r) => NameMatches(w.FullName, r.FullName, r.Aliases),
            (AvroFixed w, AvroFixed r) => NameMatches(w.FullName, r.FullName, r.Aliases),
            (AvroArray, AvroArray) => true,
            (AvroMap, AvroMap) => true,
            _ => false,
        };
    }

    private static string Describe(AvroType type) => type switch
    {
        AvroPrimitive p => $"'{p.Name}'",
        AvroRecord r => $"record '{r.FullName}'",
        AvroEnum e => $"enum '{e.FullName}'",
        AvroFixed f => $"fixed '{f.FullName}'",
        AvroArray => "an array",
        AvroMap => "a map",
        AvroUnion u => $"a union of {u.Branches.Count} branch(es)",
        AvroNamedRef n => $"'{n.FullName}'",
        _ => "an unknown type",
    };
}
