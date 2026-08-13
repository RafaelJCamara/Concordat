using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Concordat.Contracts.Generators;

/// <summary>A member that could not be mapped, and why.</summary>
/// <param name="Member">The property or field.</param>
/// <param name="TypeName">The member type that has no mapping.</param>
internal readonly record struct UnsupportedMember(ISymbol Member, string TypeName);

/// <summary>
/// Turns a C# type into a JSON Schema, from Roslyn symbols alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Symbols, not reflection.</b> The alternative — an MSBuild task that loads the compiled
/// assembly — has to resolve the target's whole dependency graph inside the build process, and
/// fails in ways that depend on the consumer's package set rather than on anything Concordat
/// controls. Reading symbols needs nothing but the compilation that is already in memory.
/// </para>
/// <para>
/// <b>Nullability is the contract.</b> A non-nullable member is required and a nullable one is
/// optional, because that is what the C# already says and a second annotation to keep in sync
/// would immediately fall out of sync. The consequence is worth stating plainly: enabling
/// nullable reference types on an existing project changes the generated schema, and the drift
/// check will say so.
/// </para>
/// </remarks>
internal sealed class SchemaBuilder
{
    private readonly List<UnsupportedMember> _unsupported = [];

    /// <summary>Members the mapper could not represent.</summary>
    public IReadOnlyList<UnsupportedMember> Unsupported => _unsupported;

    /// <summary>Builds the schema document for a contract type.</summary>
    /// <param name="type">The annotated type.</param>
    /// <param name="description">An optional description for the root.</param>
    /// <returns>The schema, as compact JSON with deterministic ordering.</returns>
    public string Build(INamedTypeSymbol type, string? description)
    {
        var node = Describe(type, NullableAnnotation.NotAnnotated, new HashSet<string>(StringComparer.Ordinal));

        if (!string.IsNullOrEmpty(description))
        {
            node = node with { Description = description };
        }

        var json = new StringBuilder();
        node.Write(json);
        return json.ToString();
    }

    private JsonNode Describe(ITypeSymbol type, NullableAnnotation annotation, HashSet<string> seen)
    {
        var nullable = annotation is NullableAnnotation.Annotated;

        // Nullable<T> is the value-type spelling of the same idea.
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } value)
        {
            return Describe(value.TypeArguments[0], NullableAnnotation.NotAnnotated, seen) with
            {
                Nullable = true,
            };
        }

        var primitive = Primitive(type);
        if (primitive is not null)
        {
            return primitive with { Nullable = nullable };
        }

        if (type is IArrayTypeSymbol array)
        {
            return new JsonNode("array", nullable)
            {
                Items = Describe(array.ElementType, array.ElementNullableAnnotation, seen),
            };
        }

        if (type is INamedTypeSymbol named)
        {
            if (named.TypeKind is TypeKind.Enum)
            {
                // Names, not ordinals. An enum's numeric values are an implementation detail
                // that reordering silently changes; the names are what the wire should carry.
                return new JsonNode("string", nullable)
                {
                    Enum = [.. named.GetMembers().OfType<IFieldSymbol>()
                        .Where(f => f.HasConstantValue)
                        .Select(f => f.Name)
                        .OrderBy(n => n, StringComparer.Ordinal)],
                };
            }

            if (TryDictionary(named, out var valueType, out var valueAnnotation))
            {
                return new JsonNode("object", nullable)
                {
                    AdditionalProperties = Describe(valueType!, valueAnnotation, seen),
                };
            }

            if (TryEnumerable(named, out var elementType, out var elementAnnotation))
            {
                return new JsonNode("array", nullable)
                {
                    Items = Describe(elementType!, elementAnnotation, seen),
                };
            }

            return DescribeObject(named, nullable, seen);
        }

        return new JsonNode(null, nullable);
    }

    private JsonNode DescribeObject(INamedTypeSymbol type, bool nullable, HashSet<string> seen)
    {
        var key = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (!seen.Add(key))
        {
            // A self-referencing type. Emitting an unconstrained object terminates the
            // recursion honestly; inventing a $ref would commit every other SDK to resolving
            // it the same way, which ADR-019 warns against.
            return new JsonNode("object", nullable);
        }

        var properties = new List<(string Name, JsonNode Node, bool Required)>();

        foreach (var member in type.GetMembers())
        {
            if (member.DeclaredAccessibility is not Accessibility.Public || member.IsStatic)
            {
                continue;
            }

            ITypeSymbol memberType;
            NullableAnnotation memberAnnotation;
            bool required;

            switch (member)
            {
                case IPropertySymbol { GetMethod: not null, IsIndexer: false } property:
                    memberType = property.Type;
                    memberAnnotation = property.NullableAnnotation;
                    required = property.IsRequired || memberAnnotation is not NullableAnnotation.Annotated;
                    break;

                case IFieldSymbol { AssociatedSymbol: null } field when !field.IsConst:
                    memberType = field.Type;
                    memberAnnotation = field.NullableAnnotation;
                    required = field.IsRequired || memberAnnotation is not NullableAnnotation.Annotated;
                    break;

                default:
                    continue;
            }

            var node = Describe(memberType, memberAnnotation, seen);

            if (node.Type is null && node.Enum is null && node.Properties is null)
            {
                _unsupported.Add(new UnsupportedMember(member, memberType.ToDisplayString()));
            }

            properties.Add((JsonName(member.Name), node, required));
        }

        seen.Remove(key);

        // Sorted, so the generated document is byte-stable across compilations. Member order
        // in C# is source order, and a developer moving a property would otherwise register as
        // a schema change.
        properties.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        return new JsonNode("object", nullable)
        {
            Properties = properties.Select(p => (p.Name, p.Node)).ToList(),
            Required = [.. properties.Where(p => p.Required).Select(p => p.Name)],
        };
    }

    /// <summary>Matches System.Text.Json's default camelCase policy.</summary>
    private static string JsonName(string name) =>
        name.Length == 0 || char.IsLower(name[0])
            ? name
            : char.ToLowerInvariant(name[0]) + name.Substring(1);

    private static JsonNode? Primitive(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_String or SpecialType.System_Char:
                return new JsonNode("string", false);

            case SpecialType.System_Boolean:
                return new JsonNode("boolean", false);

            case SpecialType.System_Byte or SpecialType.System_SByte
                or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32
                or SpecialType.System_Int64 or SpecialType.System_UInt64:
                return new JsonNode("integer", false);

            case SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal:
                return new JsonNode("number", false);

            case SpecialType.System_Object:
                return new JsonNode(null, false);

            default:
                break;
        }

        return type.ToDisplayString() switch
        {
            "System.Guid" => new JsonNode("string", false) { Format = "uuid" },
            "System.DateTime" or "System.DateTimeOffset" => new JsonNode("string", false) { Format = "date-time" },
            "System.DateOnly" => new JsonNode("string", false) { Format = "date" },
            "System.TimeOnly" => new JsonNode("string", false) { Format = "time" },
            "System.TimeSpan" => new JsonNode("string", false) { Format = "duration" },
            "System.Uri" => new JsonNode("string", false) { Format = "uri" },

            // Deliberately absent: decimal is `number` above rather than a string. The wire
            // format is JSON, and a producer in another language will write a JSON number.
            _ => null,
        };
    }

    private static bool TryDictionary(
        INamedTypeSymbol type, out ITypeSymbol? valueType, out NullableAnnotation annotation)
    {
        foreach (var candidate in Self(type).Concat(type.AllInterfaces))
        {
            if (candidate is { IsGenericType: true, TypeArguments.Length: 2 }
                && candidate.ConstructedFrom.ToDisplayString().StartsWith(
                    "System.Collections.Generic.IDictionary<", StringComparison.Ordinal)
                && candidate.TypeArguments[0].SpecialType is SpecialType.System_String)
            {
                valueType = candidate.TypeArguments[1];
                annotation = candidate.TypeArgumentNullableAnnotations[1];
                return true;
            }
        }

        valueType = null;
        annotation = NullableAnnotation.None;
        return false;
    }

    private static bool TryEnumerable(
        INamedTypeSymbol type, out ITypeSymbol? elementType, out NullableAnnotation annotation)
    {
        // String is IEnumerable<char> and must not become an array.
        if (type.SpecialType is not SpecialType.System_String)
        {
            foreach (var candidate in Self(type).Concat(type.AllInterfaces))
            {
                if (candidate is { IsGenericType: true, TypeArguments.Length: 1 }
                    && candidate.ConstructedFrom.SpecialType
                        is SpecialType.System_Collections_Generic_IEnumerable_T)
                {
                    elementType = candidate.TypeArguments[0];
                    annotation = candidate.TypeArgumentNullableAnnotations[0];
                    return true;
                }
            }
        }

        elementType = null;
        annotation = NullableAnnotation.None;
        return false;
    }

    private static IEnumerable<INamedTypeSymbol> Self(INamedTypeSymbol type)
    {
        yield return type;
    }

    /// <summary>A JSON Schema node, written by hand so the output is byte-stable.</summary>
    private sealed record JsonNode(string? Type, bool Nullable)
    {
        public string? Format { get; init; }

        public string? Description { get; init; }

        public List<string>? Enum { get; init; }

        public List<(string Name, JsonNode Node)>? Properties { get; init; }

        public List<string>? Required { get; init; }

        public JsonNode? Items { get; init; }

        public JsonNode? AdditionalProperties { get; init; }

        public void Write(StringBuilder json)
        {
            json.Append('{');
            var first = true;

            if (Type is not null)
            {
                Comma(json, ref first);
                json.Append("\"type\":");
                json.Append(Nullable ? $"[\"{Type}\",\"null\"]" : $"\"{Type}\"");
            }

            if (Format is not null)
            {
                Comma(json, ref first);
                json.Append("\"format\":\"").Append(Format).Append('"');
            }

            if (Description is not null)
            {
                Comma(json, ref first);
                json.Append("\"description\":");
                Escape(json, Description);
            }

            if (Enum is not null)
            {
                Comma(json, ref first);
                json.Append("\"enum\":[");
                for (var i = 0; i < Enum.Count; i++)
                {
                    if (i > 0)
                    {
                        json.Append(',');
                    }

                    Escape(json, Enum[i]);
                }

                json.Append(']');
            }

            if (Properties is { Count: > 0 })
            {
                Comma(json, ref first);
                json.Append("\"properties\":{");
                for (var i = 0; i < Properties.Count; i++)
                {
                    if (i > 0)
                    {
                        json.Append(',');
                    }

                    Escape(json, Properties[i].Name);
                    json.Append(':');
                    Properties[i].Node.Write(json);
                }

                json.Append('}');
            }

            if (Required is { Count: > 0 })
            {
                Comma(json, ref first);
                json.Append("\"required\":[");
                for (var i = 0; i < Required.Count; i++)
                {
                    if (i > 0)
                    {
                        json.Append(',');
                    }

                    Escape(json, Required[i]);
                }

                json.Append(']');
            }

            if (Items is not null)
            {
                Comma(json, ref first);
                json.Append("\"items\":");
                Items.Write(json);
            }

            if (AdditionalProperties is not null)
            {
                Comma(json, ref first);
                json.Append("\"additionalProperties\":");
                AdditionalProperties.Write(json);
            }

            json.Append('}');
        }

        private static void Comma(StringBuilder json, ref bool first)
        {
            if (!first)
            {
                json.Append(',');
            }

            first = false;
        }

        private static void Escape(StringBuilder json, string value)
        {
            json.Append('"');

            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': json.Append("\\\""); break;
                    case '\\': json.Append("\\\\"); break;
                    case '\n': json.Append("\\n"); break;
                    case '\r': json.Append("\\r"); break;
                    case '\t': json.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            json.Append(c);
                        }

                        break;
                }
            }

            json.Append('"');
        }
    }
}
