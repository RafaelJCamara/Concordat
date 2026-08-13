using System.Globalization;

namespace Concordat.Formats.Protobuf;

/// <summary>
/// Parses proto3 source into <see cref="ProtoFile"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hand-written, like the JSON and Avro canonicalisers, and for the same two reasons.</b>
/// ADR-019 requires every SDK to reproduce canonicalisation byte-for-byte, and a library's own
/// notion of a normalised descriptor cannot be audited for that. More concretely, the only
/// mature .NET <c>.proto</c> parsers are reflection-heavy, and M3.3 established that
/// reflection-based code in a NativeAOT binary fails <em>silently</em> — it compiles, passes
/// every JIT test, and then misbehaves once trimmed. The CLI links this assembly.
/// </para>
/// <para>
/// <b>It refuses what it does not understand rather than guessing.</b> proto2, groups, extend
/// blocks, services and aggregate option values are rejected with a clear message. A parser
/// that silently mis-reads a construct produces a confidently wrong schema id and a
/// confidently wrong compatibility verdict, which is worse than not supporting the format —
/// the same reasoning as M2.3's refusal to invent a spelling for generic type names.
/// </para>
/// </remarks>
internal static class ProtoParser
{
    private static readonly HashSet<string> ScalarTypes = new(StringComparer.Ordinal)
    {
        "double", "float", "int32", "int64", "uint32", "uint64", "sint32", "sint64",
        "fixed32", "fixed64", "sfixed32", "sfixed64", "bool", "string", "bytes",
    };

    /// <summary>Parses a proto3 document.</summary>
    /// <param name="source">The <c>.proto</c> text.</param>
    /// <returns>The parsed file.</returns>
    /// <exception cref="MalformedProtoSchemaException">The document is not valid proto3.</exception>
    public static ProtoFile Parse(string source)
    {
        var cursor = new Cursor(Lexer.Tokenize(source));

        string? package = null;
        var imports = new List<string>();
        var options = new List<ProtoOption>();
        var messages = new List<PendingMessage>();
        var enums = new List<PendingEnum>();
        var sawSyntax = false;

        while (!cursor.AtEnd)
        {
            var keyword = cursor.Peek();
            switch (keyword)
            {
                case "syntax":
                    cursor.Take();
                    cursor.Expect("=");
                    var syntax = cursor.TakeString();
                    if (!string.Equals(syntax, "proto3", StringComparison.Ordinal))
                    {
                        throw new MalformedProtoSchemaException(
                            $"Only proto3 is supported; this document declares '{syntax}'. " +
                            "proto2 adds required fields, groups and default values, none of " +
                            "which this engine reasons about — refusing is safer than " +
                            "producing a verdict that ignores them.");
                    }

                    cursor.Expect(";");
                    sawSyntax = true;
                    break;

                case "package":
                    cursor.Take();
                    package = cursor.TakeQualifiedName();
                    cursor.Expect(";");
                    break;

                case "import":
                    cursor.Take();
                    // 'public' and 'weak' change visibility, not content.
                    if (cursor.Peek() is "public" or "weak")
                    {
                        cursor.Take();
                    }

                    imports.Add(cursor.TakeString());
                    cursor.Expect(";");
                    break;

                case "option":
                    cursor.Take();
                    options.Add(ParseOption(cursor));
                    break;

                case "message":
                    cursor.Take();
                    messages.Add(ParseMessage(cursor));
                    break;

                case "enum":
                    cursor.Take();
                    enums.Add(ParseEnum(cursor));
                    break;

                case ";":
                    cursor.Take();
                    break;

                case "service":
                case "extend":
                    throw new MalformedProtoSchemaException(
                        $"'{keyword}' is not supported. Concordat registers message schemas; " +
                        "an RPC surface is not one, and extensions change how unknown fields " +
                        "are interpreted in ways this engine does not model.");

                default:
                    throw new MalformedProtoSchemaException(
                        $"Unexpected '{keyword}' at the top level of the file.");
            }
        }

        if (!sawSyntax)
        {
            // Without the declaration protoc assumes proto2, so an absent syntax line is not a
            // proto3 document that forgot to say so.
            throw new MalformedProtoSchemaException(
                "Missing 'syntax = \"proto3\";'. Without it the file is proto2 by definition.");
        }

        var prefix = package is null ? "" : $".{package}";

        // Every name the file declares, gathered before anything is resolved: a message may
        // reference a sibling declared after it, so a single pass in declaration order would
        // fail to resolve forward references.
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            CollectDefined(message, prefix, defined);
        }

        foreach (var @enum in enums)
        {
            defined.Add($"{prefix}.{@enum.Name}");
        }

        return new ProtoFile(
            package,
            imports,
            [.. messages.Select(m => ResolveWithin(m, prefix, defined))],
            [.. enums.Select(e => ResolveEnum(e, prefix))],
            options);
    }

    // ------------------------------------------------------------------ definitions

    private sealed record PendingMessage(
        string Name,
        List<ProtoField> Fields,
        List<ProtoOneof> Oneofs,
        List<PendingEnum> Enums,
        List<PendingMessage> Messages,
        ProtoReserved Reserved,
        List<ProtoOption> Options);

    private sealed record PendingEnum(
        string Name,
        List<ProtoEnumValue> Values,
        ProtoReserved Reserved,
        List<ProtoOption> Options);

    private static PendingMessage ParseMessage(Cursor cursor)
    {
        var name = cursor.TakeIdentifier();
        cursor.Expect("{");

        var fields = new List<ProtoField>();
        var oneofs = new List<ProtoOneof>();
        var enums = new List<PendingEnum>();
        var messages = new List<PendingMessage>();
        var options = new List<ProtoOption>();
        var ranges = new List<ProtoReservedRange>();
        var reservedNames = new List<string>();

        while (cursor.Peek() != "}")
        {
            switch (cursor.Peek())
            {
                case "message":
                    cursor.Take();
                    messages.Add(ParseMessage(cursor));
                    break;

                case "enum":
                    cursor.Take();
                    enums.Add(ParseEnum(cursor));
                    break;

                case "oneof":
                    cursor.Take();
                    oneofs.Add(ParseOneof(cursor));
                    break;

                case "option":
                    cursor.Take();
                    options.Add(ParseOption(cursor));
                    break;

                case "reserved":
                    cursor.Take();
                    ParseReserved(cursor, ranges, reservedNames);
                    break;

                case "extensions":
                case "group":
                    throw new MalformedProtoSchemaException(
                        $"'{cursor.Peek()}' is a proto2 construct and is not supported.");

                case ";":
                    cursor.Take();
                    break;

                default:
                    fields.Add(ParseField(cursor, oneofName: null));
                    break;
            }
        }

        cursor.Expect("}");

        return new PendingMessage(
            name, fields, oneofs, enums, messages,
            new ProtoReserved(ranges, reservedNames), options);
    }

    private static ProtoOneof ParseOneof(Cursor cursor)
    {
        var name = cursor.TakeIdentifier();
        cursor.Expect("{");

        var fields = new List<ProtoField>();
        while (cursor.Peek() != "}")
        {
            if (cursor.Peek() is ";")
            {
                cursor.Take();
                continue;
            }

            if (cursor.Peek() is "option")
            {
                cursor.Take();
                ParseOption(cursor);
                continue;
            }

            fields.Add(ParseField(cursor, name));
        }

        cursor.Expect("}");
        return new ProtoOneof(name, fields);
    }

    private static PendingEnum ParseEnum(Cursor cursor)
    {
        var name = cursor.TakeIdentifier();
        cursor.Expect("{");

        var values = new List<ProtoEnumValue>();
        var options = new List<ProtoOption>();
        var ranges = new List<ProtoReservedRange>();
        var reservedNames = new List<string>();

        while (cursor.Peek() != "}")
        {
            switch (cursor.Peek())
            {
                case "option":
                    cursor.Take();
                    options.Add(ParseOption(cursor));
                    break;

                case "reserved":
                    cursor.Take();
                    ParseReserved(cursor, ranges, reservedNames);
                    break;

                case ";":
                    cursor.Take();
                    break;

                default:
                    var valueName = cursor.TakeIdentifier();
                    cursor.Expect("=");
                    var number = cursor.TakeInteger();
                    var valueOptions = ParseInlineOptions(cursor);
                    cursor.Expect(";");
                    values.Add(new ProtoEnumValue(valueName, number, valueOptions));
                    break;
            }
        }

        cursor.Expect("}");
        return new PendingEnum(name, values, new ProtoReserved(ranges, reservedNames), options);
    }

    private static ProtoField ParseField(Cursor cursor, string? oneofName)
    {
        var label = ProtoLabel.Singular;
        if (cursor.Peek() is "repeated")
        {
            cursor.Take();
            label = ProtoLabel.Repeated;
        }
        else if (cursor.Peek() is "optional")
        {
            cursor.Take();
            label = ProtoLabel.Optional;
        }
        else if (cursor.Peek() is "required")
        {
            throw new MalformedProtoSchemaException(
                "'required' is proto2 only and is not supported.");
        }

        string typeName;
        string? mapKey = null;

        if (cursor.Peek() is "map")
        {
            cursor.Take();
            cursor.Expect("<");
            mapKey = cursor.TakeQualifiedName();
            cursor.Expect(",");
            typeName = cursor.TakeTypeReference();
            cursor.Expect(">");

            if (label is ProtoLabel.Repeated)
            {
                throw new MalformedProtoSchemaException("A map field cannot be 'repeated'.");
            }
        }
        else
        {
            typeName = cursor.TakeTypeReference();
        }

        var name = cursor.TakeIdentifier();
        cursor.Expect("=");
        var number = cursor.TakeInteger();

        if (number < 1)
        {
            throw new MalformedProtoSchemaException(
                $"Field '{name}' has number {number}; field numbers start at 1.");
        }

        var options = ParseInlineOptions(cursor);
        cursor.Expect(";");

        return new ProtoField(name, number, typeName, label, mapKey, options, oneofName);
    }

    private static void ParseReserved(
        Cursor cursor, List<ProtoReservedRange> ranges, List<string> names)
    {
        while (true)
        {
            if (cursor.PeekIsString())
            {
                names.Add(cursor.TakeString());
            }
            else
            {
                var start = cursor.TakeInteger();
                var end = start;

                if (cursor.Peek() is "to")
                {
                    cursor.Take();
                    // 'max' is 536,870,911 - the largest legal field number.
                    end = cursor.Peek() is "max" ? Max(cursor) : cursor.TakeInteger();
                }

                ranges.Add(new ProtoReservedRange(start, end));
            }

            if (cursor.Peek() is ",")
            {
                cursor.Take();
                continue;
            }

            break;
        }

        cursor.Expect(";");

        static int Max(Cursor cursor)
        {
            cursor.Take();
            return 536870911;
        }
    }

    private static ProtoOption ParseOption(Cursor cursor)
    {
        var name = cursor.TakeOptionName();
        cursor.Expect("=");
        var value = cursor.TakeOptionValue();
        cursor.Expect(";");
        return new ProtoOption(name, value);
    }

    private static List<ProtoOption> ParseInlineOptions(Cursor cursor)
    {
        var options = new List<ProtoOption>();
        if (cursor.Peek() is not "[")
        {
            return options;
        }

        cursor.Take();
        while (true)
        {
            var name = cursor.TakeOptionName();
            cursor.Expect("=");
            options.Add(new ProtoOption(name, cursor.TakeOptionValue()));

            if (cursor.Peek() is ",")
            {
                cursor.Take();
                continue;
            }

            break;
        }

        cursor.Expect("]");
        return options;
    }

    // -------------------------------------------------------------------- resolution

    /// <summary>
    /// Turns declared names into fullnames and resolves every type reference it can.
    /// </summary>
    /// <remarks>
    /// A reference is resolved against protobuf's innermost-first scoping and rewritten to a
    /// leading-dot fullname, so <c>Item</c>, <c>Order.Item</c> and <c>.acme.Order.Item</c>
    /// canonicalise identically. A reference this document does not define — anything reached
    /// through an <c>import</c> — is left exactly as written, because resolving it needs the
    /// imported file, which is DECISIONS-PENDING #16.
    /// </remarks>
    private static void CollectDefined(PendingMessage message, string scope, HashSet<string> into)
    {
        var fullName = $"{scope}.{message.Name}";
        into.Add(fullName);

        foreach (var nested in message.Messages)
        {
            CollectDefined(nested, fullName, into);
        }

        foreach (var @enum in message.Enums)
        {
            into.Add($"{fullName}.{@enum.Name}");
        }
    }

    private static ProtoMessage ResolveWithin(
        PendingMessage message, string scope, HashSet<string> defined)
    {
        var fullName = $"{scope}.{message.Name}";

        return new ProtoMessage(
            fullName,
            [.. message.Fields.Select(f => ResolveField(f, fullName, defined))],
            [.. message.Oneofs.Select(o => new ProtoOneof(
                o.Name,
                [.. o.Fields.Select(f => ResolveField(f, fullName, defined))]))],
            [.. message.Enums.Select(e => ResolveEnum(e, fullName))],
            [.. message.Messages.Select(m => ResolveWithin(m, fullName, defined))],
            message.Reserved,
            message.Options);
    }

    private static ProtoEnum ResolveEnum(PendingEnum @enum, string scope) =>
        new($"{scope}.{@enum.Name}", @enum.Values, @enum.Reserved, @enum.Options);

    private static ProtoField ResolveField(
        ProtoField field, string scope, HashSet<string> defined) =>
        field with
        {
            TypeName = ResolveTypeName(field.TypeName, scope, defined),
            MapKeyType = field.MapKeyType,
        };

    private static string ResolveTypeName(string name, string scope, HashSet<string> defined)
    {
        if (ScalarTypes.Contains(name))
        {
            return name;
        }

        if (name.StartsWith('.'))
        {
            return name;
        }

        // Innermost-first, exactly as protoc resolves: try the enclosing scope, then each
        // enclosing scope outward, then the file's package root.
        var current = scope;
        while (true)
        {
            var candidate = $"{current}.{name}";
            if (defined.Contains(candidate))
            {
                return candidate;
            }

            var lastDot = current.LastIndexOf('.');
            if (lastDot <= 0)
            {
                break;
            }

            current = current[..lastDot];
        }

        return defined.Contains($".{name}") ? $".{name}" : name;
    }

    // ----------------------------------------------------------------------- lexer

    private sealed class Cursor(IReadOnlyList<Token> tokens)
    {
        private int _index;

        public bool AtEnd => _index >= tokens.Count;

        public string Peek() => AtEnd ? "" : tokens[_index].Text;

        public bool PeekIsString() => !AtEnd && tokens[_index].Kind is TokenKind.String;

        public string Take()
        {
            if (AtEnd)
            {
                throw new MalformedProtoSchemaException("Unexpected end of file.");
            }

            return tokens[_index++].Text;
        }

        public void Expect(string text)
        {
            var actual = Take();
            if (!string.Equals(actual, text, StringComparison.Ordinal))
            {
                throw new MalformedProtoSchemaException($"Expected '{text}' but found '{actual}'.");
            }
        }

        public string TakeIdentifier()
        {
            if (AtEnd || tokens[_index].Kind is not TokenKind.Identifier)
            {
                throw new MalformedProtoSchemaException(
                    $"Expected an identifier but found '{Peek()}'.");
            }

            return Take();
        }

        public string TakeString()
        {
            if (AtEnd || tokens[_index].Kind is not TokenKind.String)
            {
                throw new MalformedProtoSchemaException(
                    $"Expected a quoted string but found '{Peek()}'.");
            }

            return Take();
        }

        public int TakeInteger()
        {
            var text = Take();
            if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            {
                throw new MalformedProtoSchemaException($"Expected an integer but found '{text}'.");
            }

            return value;
        }

        /// <summary>Reads a dotted name such as <c>acme.orders</c>.</summary>
        public string TakeQualifiedName()
        {
            var name = TakeIdentifier();
            while (Peek() is ".")
            {
                Take();
                name += $".{TakeIdentifier()}";
            }

            return name;
        }

        /// <summary>Reads a type reference, which may carry a leading dot.</summary>
        public string TakeTypeReference()
        {
            var leading = "";
            if (Peek() is ".")
            {
                Take();
                leading = ".";
            }

            return leading + TakeQualifiedName();
        }

        /// <summary>Reads an option name, including the parenthesised extension form.</summary>
        public string TakeOptionName()
        {
            string name;
            if (Peek() is "(")
            {
                Take();
                name = $"({TakeTypeReference()})";
                Expect(")");
            }
            else
            {
                name = TakeIdentifier();
            }

            while (Peek() is ".")
            {
                Take();
                name += $".{TakeIdentifier()}";
            }

            return name;
        }

        /// <summary>Reads an option value, preserving how it was written.</summary>
        public string TakeOptionValue()
        {
            if (Peek() is "{")
            {
                throw new MalformedProtoSchemaException(
                    "Aggregate option values are not supported. They would have to be " +
                    "normalised to compare reliably, and no compatibility rule reads one.");
            }

            return PeekIsString() ? $"\"{Escape(Take())}\"" : Take();
        }

        private static string Escape(string value) =>
            value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private enum TokenKind
    {
        Identifier,
        Number,
        String,
        Symbol,
    }

    private sealed record Token(TokenKind Kind, string Text);

    private static class Lexer
    {
        public static List<Token> Tokenize(string source)
        {
            var tokens = new List<Token>();
            var i = 0;

            while (i < source.Length)
            {
                var c = source[i];

                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (c is '/' && i + 1 < source.Length)
                {
                    if (source[i + 1] is '/')
                    {
                        while (i < source.Length && source[i] is not '\n')
                        {
                            i++;
                        }

                        continue;
                    }

                    if (source[i + 1] is '*')
                    {
                        var close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        if (close < 0)
                        {
                            throw new MalformedProtoSchemaException("Unterminated block comment.");
                        }

                        i = close + 2;
                        continue;
                    }
                }

                if (c is '"' or '\'')
                {
                    i = ReadString(source, i, c, tokens);
                    continue;
                }

                if (char.IsLetter(c) || c is '_')
                {
                    var start = i;
                    while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] is '_'))
                    {
                        i++;
                    }

                    tokens.Add(new Token(TokenKind.Identifier, source[start..i]));
                    continue;
                }

                if (char.IsDigit(c) || (c is '-' && i + 1 < source.Length && char.IsDigit(source[i + 1])))
                {
                    var start = i;
                    i++;
                    while (i < source.Length &&
                           (char.IsLetterOrDigit(source[i]) || source[i] is '.' or '+' or '-'))
                    {
                        // Hex, floats and exponents all fall out of this; the value is only
                        // interpreted where a rule needs a number.
                        i++;
                    }

                    tokens.Add(new Token(TokenKind.Number, source[start..i]));
                    continue;
                }

                tokens.Add(new Token(TokenKind.Symbol, c.ToString()));
                i++;
            }

            return tokens;
        }

        private static int ReadString(string source, int i, char quote, List<Token> tokens)
        {
            var value = new System.Text.StringBuilder();
            i++;

            while (i < source.Length && source[i] != quote)
            {
                if (source[i] is '\\' && i + 1 < source.Length)
                {
                    value.Append(source[i + 1] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        var other => other,
                    });

                    i += 2;
                    continue;
                }

                value.Append(source[i]);
                i++;
            }

            if (i >= source.Length)
            {
                throw new MalformedProtoSchemaException("Unterminated string literal.");
            }

            tokens.Add(new Token(TokenKind.String, value.ToString()));
            return i + 1;
        }
    }
}
