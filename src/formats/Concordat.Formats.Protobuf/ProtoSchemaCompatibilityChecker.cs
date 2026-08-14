using System.Globalization;
using Concordat.Domain.Registry;
using Concordat.Formats.Abstractions;

namespace Concordat.Formats.Protobuf;

/// <summary>
/// Decides whether a proposed proto3 schema may follow the versions already registered.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is where the two-axis model earns its place.</b> DESIGN §4 names a documented
/// Confluent defect: its Protobuf checker is <em>stricter than Protobuf's actual wire
/// compatibility</em> and rejects changes that produce byte-identical output — renaming a
/// message while keeping field tags is the standing example. With a single axis there is
/// nowhere to record "the bytes are fine, the JSON mapping is not", so the only available
/// answer is to reject. The surface axis says it precisely instead.
/// </para>
/// <para>
/// <b>Field numbers are identity; names are not on the wire at all.</b> Everything here
/// follows from that. Fields are matched by number, never by name, and a name change is a
/// finding about the JSON mapping and generated code rather than about decoding.
/// </para>
/// <para>The surface ladder used throughout:</para>
/// <list type="bullet">
///   <item><description><see cref="CompatibilitySurface.Wire"/> — the bytes fail to decode, or
///     decode to a <em>different value</em> (a zigzag or signedness change).</description></item>
///   <item><description><see cref="CompatibilitySurface.WireJson"/> — the value survives but
///     its proto3 JSON representation changes. Renames land here, and so does
///     <c>int32 → int64</c>: proto3 JSON encodes 64-bit integers as <b>strings</b> and 32-bit
///     as numbers.</description></item>
///   <item><description><see cref="CompatibilitySurface.Source"/> — bytes and JSON both hold,
///     and only generated code changes.</description></item>
/// </list>
/// <para>
/// <b>Bidirectional breaks are reported once per direction, deliberately.</b> Unlike Avro,
/// Protobuf is not reader/writer asymmetric: a wire-type change breaks old and new readers
/// alike. Emitting such a finding under one direction only would let a <c>Forward</c> policy
/// miss it entirely, so both are emitted and the mode filter keeps whichever apply.
/// </para>
/// </remarks>
public sealed class ProtoSchemaCompatibilityChecker : ICompatibilityChecker
{
    /// <summary>
    /// The four wire types a field can occupy. Changing class is unconditionally breaking:
    /// a decoder reads the tag, learns the class, and skips or parses accordingly.
    /// </summary>
    private enum WireClass
    {
        Varint,
        Fixed64,
        Fixed32,
        LengthDelimited,
    }

    /// <summary>
    /// How a scalar renders in proto3 JSON. Two types in the same wire class but different
    /// JSON kinds are a <see cref="CompatibilitySurface.WireJson"/> change, not a
    /// <see cref="CompatibilitySurface.Source"/> one.
    /// </summary>
    private enum JsonKind
    {
        Number,

        /// <summary>64-bit integers, which proto3 JSON emits as quoted strings.</summary>
        NumericString,
        Boolean,
        String,

        /// <summary>Bytes, which proto3 JSON emits base64-encoded.</summary>
        Base64,
        Composite,
    }

    private static readonly Dictionary<string, WireClass> WireClasses = new(StringComparer.Ordinal)
    {
        ["int32"] = WireClass.Varint,
        ["int64"] = WireClass.Varint,
        ["uint32"] = WireClass.Varint,
        ["uint64"] = WireClass.Varint,
        ["sint32"] = WireClass.Varint,
        ["sint64"] = WireClass.Varint,
        ["bool"] = WireClass.Varint,
        ["fixed64"] = WireClass.Fixed64,
        ["sfixed64"] = WireClass.Fixed64,
        ["double"] = WireClass.Fixed64,
        ["fixed32"] = WireClass.Fixed32,
        ["sfixed32"] = WireClass.Fixed32,
        ["float"] = WireClass.Fixed32,
        ["string"] = WireClass.LengthDelimited,
        ["bytes"] = WireClass.LengthDelimited,
    };

    private static readonly Dictionary<string, JsonKind> JsonKinds = new(StringComparer.Ordinal)
    {
        ["int32"] = JsonKind.Number,
        ["uint32"] = JsonKind.Number,
        ["sint32"] = JsonKind.Number,
        ["fixed32"] = JsonKind.Number,
        ["sfixed32"] = JsonKind.Number,
        ["float"] = JsonKind.Number,
        ["double"] = JsonKind.Number,
        ["int64"] = JsonKind.NumericString,
        ["uint64"] = JsonKind.NumericString,
        ["sint64"] = JsonKind.NumericString,
        ["fixed64"] = JsonKind.NumericString,
        ["sfixed64"] = JsonKind.NumericString,
        ["bool"] = JsonKind.Boolean,
        ["string"] = JsonKind.String,
        ["bytes"] = JsonKind.Base64,
    };

    /// <summary>
    /// Varint types whose bytes mean different numbers. <c>sint*</c> uses zigzag encoding, and
    /// the signed/unsigned split reinterprets the same bits for negatives.
    /// </summary>
    private static readonly Dictionary<string, string> VarintEncodings = new(StringComparer.Ordinal)
    {
        ["int32"] = "twos-complement",
        ["int64"] = "twos-complement",
        ["uint32"] = "unsigned",
        ["uint64"] = "unsigned",
        ["sint32"] = "zigzag",
        ["sint64"] = "zigzag",
        ["bool"] = "boolean",
    };

    /// <inheritdoc />
    public SchemaFormat Format => SchemaFormat.Protobuf;

    /// <inheritdoc />
    public CompatibilityReport Check(
        string proposedCanonicalBody,
        IReadOnlyList<PriorSchema> priors,
        CompatibilityPolicy policy,
        ContentModel contentModel)
    {
        ArgumentNullException.ThrowIfNull(proposedCanonicalBody);
        ArgumentNullException.ThrowIfNull(priors);

        if (priors.Count == 0 || !policy.IsChecked)
        {
            return new CompatibilityReport(true, [], [], SemverBump.None);
        }

        var proposed = ProtoParser.Parse(proposedCanonicalBody);
        var divergences = new List<BreakingChange>();

        foreach (var prior in SelectPriors(priors, policy.Mode))
        {
            var previous = ProtoParser.Parse(prior.CanonicalBody);

            foreach (var divergence in Compare(previous, proposed))
            {
                if (!AppliesTo(divergence.Direction, policy.Mode))
                {
                    continue;
                }

                divergences.Add(new BreakingChange(
                    divergence.Path,
                    divergence.Kind,
                    divergence.Direction,
                    divergence.Surface,
                    divergence.Message,
                    prior.Ordinal));
            }
        }

        var violations = divergences.Where(d => policy.IsViolatedBy(d.Surface)).ToList();

        var bump = violations.Count > 0
            ? SemverBump.Major
            : divergences.Count > 0
                ? SemverBump.Minor
                : SemverBump.Patch;

        return new CompatibilityReport(violations.Count == 0, violations, divergences, bump);
    }

    private sealed record Divergence(
        string Path,
        string Kind,
        CompatibilityDirection Direction,
        CompatibilitySurface Surface,
        string Message);

    // ------------------------------------------------------------------- comparison

    private static List<Divergence> Compare(ProtoFile previous, ProtoFile proposed)
    {
        var found = new List<Divergence>();

        var previousMessages = previous.AllMessages();
        var proposedMessages = proposed.AllMessages();

        foreach (var (fullName, before) in previousMessages.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (proposedMessages.TryGetValue(fullName, out var after))
            {
                CompareMessage(before, after, fullName, found);
                continue;
            }

            // The message is gone under this name. Splitting a .proto across files leaves the
            // definition reachable through an import, which this engine cannot follow yet
            // (ADR-023), so it is reported as a rename rather than a deletion.
            Both(found,
                fullName,
                BreakingChangeKinds.NameChanged,
                CompatibilitySurface.WireJson,
                $"Message '{fullName}' no longer exists under that name. Field numbers are " +
                "what the wire carries, so encoded bytes are unaffected — but proto3 JSON, " +
                "'Any' type URLs and generated code all name the message.");
        }

        CompareEnums(previous, proposed, found);
        return found;
    }

    private static void CompareMessage(
        ProtoMessage before, ProtoMessage after, string path, List<Divergence> found)
    {
        var beforeFields = before.AllFields.ToDictionary(f => f.Number);
        var afterFields = after.AllFields.ToDictionary(f => f.Number);

        foreach (var (number, field) in beforeFields.OrderBy(p => p.Key))
        {
            if (!afterFields.TryGetValue(number, out var proposedField))
            {
                CompareRemoval(after, field, path, found);
                continue;
            }

            CompareField(field, proposedField, $"{path}/{number.ToString(CultureInfo.InvariantCulture)}", found);
        }

        foreach (var (number, field) in afterFields.OrderBy(p => p.Key))
        {
            // A number the previous version reserved is now in use. Reservation exists because
            // data written before the original field was removed is still out there, so reusing
            // the number decodes those bytes as the new field.
            if (!beforeFields.ContainsKey(number) && before.Reserved.Covers(number))
            {
                found.Add(new Divergence(
                    $"{path}/{number.ToString(CultureInfo.InvariantCulture)}",
                    BreakingChangeKinds.FieldNumberReused,
                    CompatibilityDirection.Backward,
                    CompatibilitySurface.Wire,
                    $"Field '{field.Name}' takes number {number}, which the previous version " +
                    "reserved. Data written before that number was retired will decode into " +
                    "this field as the wrong type."));
            }
        }

        // Adding a field with an unused number is compatible in both directions: proto3 has no
        // required fields, and a reader skips tags it does not recognise. No finding.
    }

    private static void CompareRemoval(
        ProtoMessage after, ProtoField removed, string path, List<Divergence> found)
    {
        if (after.Reserved.Covers(removed.Number))
        {
            // Removed and reserved: the number cannot come back as something else, which is
            // the whole guarantee. Nothing to report.
            return;
        }

        found.Add(new Divergence(
            $"{path}/{removed.Number.ToString(CultureInfo.InvariantCulture)}",
            BreakingChangeKinds.FieldRemovedWithoutReserved,
            CompatibilityDirection.Backward,
            CompatibilitySurface.Wire,
            $"Field '{removed.Name}' (number {removed.Number}) was removed without reserving " +
            $"its number. Nothing breaks today — readers skip unknown tags — but a later " +
            $"version is now free to reuse {removed.Number} for a different type, and old " +
            $"data would decode into it silently. Add 'reserved {removed.Number};'."));
    }

    private static void CompareField(
        ProtoField before, ProtoField after, string path, List<Divergence> found)
    {
        if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal))
        {
            // The headline case: Confluent rejects this outright even though the bytes are
            // identical. The surface axis records what actually changed.
            Both(found,
                path,
                BreakingChangeKinds.NameChanged,
                CompatibilitySurface.WireJson,
                $"Field {before.Number} was renamed from '{before.Name}' to '{after.Name}'. " +
                "Field names are not transmitted, so encoded bytes are unchanged — but proto3 " +
                "JSON keys on the name and generated accessors change with it.");
        }

        if (before.Label != after.Label)
        {
            CompareLabel(before, after, path, found);
        }

        if (before.IsMap != after.IsMap)
        {
            Both(found,
                path,
                BreakingChangeKinds.WireTypeChanged,
                CompatibilitySurface.Wire,
                $"Field {before.Number} changed between a map and a non-map. A map encodes as " +
                "a repeated entry message, so the two are not interchangeable on the wire.");
            return;
        }

        if (before.IsMap &&
            !string.Equals(before.MapKeyType, after.MapKeyType, StringComparison.Ordinal))
        {
            CompareTypes(before.MapKeyType!, after.MapKeyType!, before.Number, $"{path}/key", found);
        }

        CompareTypes(before.TypeName, after.TypeName, before.Number, path, found);
    }

    private static void CompareLabel(
        ProtoField before, ProtoField after, string path, List<Divergence> found)
    {
        // 'optional' in proto3 only adds presence tracking; the encoding is identical to a
        // plain singular field, so moving between them costs nothing on the wire.
        var presenceOnly =
            (before.Label, after.Label) is
            (ProtoLabel.Singular, ProtoLabel.Optional) or (ProtoLabel.Optional, ProtoLabel.Singular);

        if (presenceOnly)
        {
            found.Add(new Divergence(
                path,
                BreakingChangeKinds.PresenceChanged,
                CompatibilityDirection.Backward,
                CompatibilitySurface.Source,
                $"Field {before.Number} changed between implicit and explicit presence. " +
                "Encoding is identical; generated code gains or loses a 'has' accessor."));
            return;
        }

        Both(found,
            path,
            BreakingChangeKinds.WireTypeChanged,
            CompatibilitySurface.Wire,
            $"Field {before.Number} changed cardinality. A repeated scalar is packed into one " +
            "length-delimited entry, so it does not decode as a singular field or the reverse.");
    }

    private static void CompareTypes(
        string before, string after, int number, string path, List<Divergence> found)
    {
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return;
        }

        var beforeScalar = WireClasses.TryGetValue(before, out var beforeClass);
        var afterScalar = WireClasses.TryGetValue(after, out var afterClass);

        if (!beforeScalar || !afterScalar)
        {
            // At least one side is a message or enum reference. An enum is a varint and a
            // message is length-delimited, but this engine does not resolve imported names, so
            // it cannot always tell which. Reporting a change it cannot classify as Wire is
            // the safe direction - the same call the JSON engine makes for 'pattern'.
            Both(found,
                path,
                BreakingChangeKinds.WireTypeChanged,
                CompatibilitySurface.Wire,
                $"Field {number} changed type from '{before}' to '{after}'. At least one side " +
                "is a named type, whose encoding cannot be assumed to match the other's.");
            return;
        }

        if (beforeClass != afterClass)
        {
            Both(found,
                path,
                BreakingChangeKinds.WireTypeChanged,
                CompatibilitySurface.Wire,
                $"Field {number} changed from '{before}' to '{after}', which moves it between " +
                "wire types. The tag tells a decoder how many bytes to read, so this " +
                "misaligns everything after it.");
            return;
        }

        if (beforeClass is WireClass.Varint &&
            !string.Equals(VarintEncodings[before], VarintEncodings[after], StringComparison.Ordinal))
        {
            Both(found,
                path,
                BreakingChangeKinds.WireTypeChanged,
                CompatibilitySurface.Wire,
                $"Field {number} changed from '{before}' to '{after}'. Both are varints, so " +
                $"the bytes are read — but as {VarintEncodings[after]} rather than " +
                $"{VarintEncodings[before]}, so the value that comes out is not the value that " +
                "went in.");
            return;
        }

        if (JsonKinds[before] != JsonKinds[after])
        {
            // int32 -> int64 lands here: identical varint bytes, but proto3 JSON quotes 64-bit
            // integers and leaves 32-bit ones bare. Passes a Wire policy, fails Source - which
            // is exactly what DESIGN §12 asks the corpus to demonstrate.
            Both(found,
                path,
                BreakingChangeKinds.TypePromoted,
                CompatibilitySurface.WireJson,
                $"Field {number} changed from '{before}' to '{after}'. The encoded bytes are " +
                $"unchanged, but proto3 JSON renders {before} as {Describe(JsonKinds[before])} " +
                $"and {after} as {Describe(JsonKinds[after])}.");
            return;
        }

        found.Add(new Divergence(
            path,
            BreakingChangeKinds.TypePromoted,
            CompatibilityDirection.Backward,
            CompatibilitySurface.Source,
            $"Field {number} changed from '{before}' to '{after}'. Encoding and JSON are both " +
            "unchanged; generated code changes type."));
    }

    private static void CompareEnums(ProtoFile previous, ProtoFile proposed, List<Divergence> found)
    {
        var proposedEnums = proposed.AllEnums();

        foreach (var (fullName, before) in previous.AllEnums().OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!proposedEnums.TryGetValue(fullName, out var after))
            {
                continue;
            }

            var beforeByNumber = before.Values.ToDictionary(v => v.Number, v => v.Name);
            var afterByNumber = after.Values.ToDictionary(v => v.Number, v => v.Name);

            foreach (var (number, name) in beforeByNumber.OrderBy(p => p.Key))
            {
                if (!afterByNumber.TryGetValue(number, out var afterName))
                {
                    // proto3 keeps an unrecognised enum number as-is rather than failing, so
                    // this never breaks decoding - only the name JSON emits.
                    found.Add(new Divergence(
                        $"{fullName}/{number.ToString(CultureInfo.InvariantCulture)}",
                        BreakingChangeKinds.EnumValueRemoved,
                        CompatibilityDirection.Backward,
                        CompatibilitySurface.WireJson,
                        $"Enum value '{name}' ({number}) was removed. proto3 preserves " +
                        "unknown numbers, so decoding still succeeds — but JSON emits the bare " +
                        $"number {number} where it used to emit '{name}'."));
                    continue;
                }

                if (!string.Equals(name, afterName, StringComparison.Ordinal))
                {
                    Both(found,
                        $"{fullName}/{number.ToString(CultureInfo.InvariantCulture)}",
                        BreakingChangeKinds.NameChanged,
                        CompatibilitySurface.WireJson,
                        $"Enum value {number} was renamed from '{name}' to '{afterName}'. The " +
                        "number is what the wire carries; proto3 JSON emits the name.");
                }
            }

            foreach (var (number, name) in afterByNumber.OrderBy(p => p.Key))
            {
                if (!beforeByNumber.ContainsKey(number))
                {
                    found.Add(new Divergence(
                        $"{fullName}/{number.ToString(CultureInfo.InvariantCulture)}",
                        BreakingChangeKinds.EnumValueAdded,
                        CompatibilityDirection.Forward,
                        CompatibilitySurface.WireJson,
                        $"Enum value '{name}' ({number}) was added. Readers on the previous " +
                        $"version decode it as the bare number {number} rather than a name."));
                }
            }
        }
    }

    // ------------------------------------------------------------------------ utils

    /// <summary>Records a divergence that breaks old and new readers alike.</summary>
    private static void Both(
        List<Divergence> found,
        string path,
        string kind,
        CompatibilitySurface surface,
        string message)
    {
        found.Add(new Divergence(path, kind, CompatibilityDirection.Backward, surface, message));
        found.Add(new Divergence(path, kind, CompatibilityDirection.Forward, surface, message));
    }

    private static string Describe(JsonKind kind) => kind switch
    {
        JsonKind.Number => "a number",
        JsonKind.NumericString => "a quoted string",
        JsonKind.Boolean => "a boolean",
        JsonKind.String => "a string",
        JsonKind.Base64 => "base64 text",
        _ => "a composite value",
    };

    private static List<PriorSchema> SelectPriors(
        IReadOnlyList<PriorSchema> priors, CompatibilityMode mode)
    {
        var ordered = priors.OrderByDescending(p => p.Ordinal).ToList();

        return mode is CompatibilityMode.BackwardTransitive
            or CompatibilityMode.ForwardTransitive
            or CompatibilityMode.FullTransitive
            ? ordered
            : [ordered[0]];
    }

    private static bool AppliesTo(CompatibilityDirection direction, CompatibilityMode mode) =>
        mode switch
        {
            CompatibilityMode.Backward or CompatibilityMode.BackwardTransitive =>
                direction is CompatibilityDirection.Backward,
            CompatibilityMode.Forward or CompatibilityMode.ForwardTransitive =>
                direction is CompatibilityDirection.Forward,
            CompatibilityMode.Full or CompatibilityMode.FullTransitive => true,
            _ => false,
        };
}
