using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Concordat.Contracts.Generators;

/// <summary>
/// Generates a schema per <c>[ConcordatContract]</c> type and fails the build on drift.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is M3's exit criterion.</b> A breaking change to a C# record has to fail the build
/// locally, in the editor, naming the offending member — not turn up as a quarantined message
/// after deployment.
/// </para>
/// <para>
/// One generator, used twice. The schema it produces is both compared against the checked-in
/// contract and emitted as an assembly attribute for the test-time helpers, so there is no
/// second implementation of the C#-to-JSON-Schema mapping to fall out of step with this one.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class ContractGenerator : IIncrementalGenerator
{
    private const string AttributeName = "Concordat.Contracts.ConcordatContractAttribute";
    private const string Category = "Concordat";

    /// <summary>The subject grammar, matching <c>SubjectName</c> and ADR-011 exactly.</summary>
    private static readonly Regex SubjectPattern =
        new(@"^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly DiagnosticDescriptor InvalidSubject = new(
        "CDT001",
        "Subject name is not valid",
        "'{0}' is not a valid subject name. Expected dot-separated segments of letters, digits " +
        "and underscores (ADR-011).",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedMemberType = new(
        "CDT002",
        "Member has no JSON Schema mapping",
        "Member '{0}' of type {1} has no JSON Schema mapping and is emitted unconstrained, so " +
        "the contract does not actually constrain it",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ContractDrift = new(
        "CDT003",
        "The checked-in contract no longer matches the type",
        "'{0}' has drifted from {1}. {2} Run the type's contract test, or update the file, and " +
        "commit the result.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "The C# type is the source of truth. A schema that no longer matches it would be " +
            "enforced against messages the type can no longer produce.");

    private static readonly DiagnosticDescriptor ContractMissing = new(
        "CDT004",
        "No checked-in contract for this type",
        "'{0}' has no contract file at {1}. Until one exists there is nothing for the build to " +
        "check against, so a breaking change here would go unnoticed.",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateSubject = new(
        "CDT005",
        "Two types declare the same subject",
        "'{0}' is declared by more than one type ({1}). Which schema wins would depend on " +
        "compilation order.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var contracts = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeName,
                predicate: static (_, _) => true,
                transform: static (ctx, _) => Extract(ctx))
            .Where(static c => c is not null)
            .Select(static (c, _) => c!.Value);

        // Checked-in contract files, keyed by file name so a subject can find its own.
        var files = context.AdditionalTextsProvider
            .Where(static f => f.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(static (f, token) => (
                Name: System.IO.Path.GetFileNameWithoutExtension(f.Path),
                f.Path,
                Text: f.GetText(token)?.ToString()))
            .Collect();

        context.RegisterSourceOutput(contracts.Collect().Combine(files), Emit);
    }

    private static Contract? Extract(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol type)
        {
            return null;
        }

        var attribute = context.Attributes[0];
        var subject = attribute.ConstructorArguments.Length > 0
            ? attribute.ConstructorArguments[0].Value as string
            : null;

        var description = attribute.NamedArguments
            .FirstOrDefault(a => a.Key == "Description").Value.Value as string;

        var builder = new SchemaBuilder();
        var schema = builder.Build(type, description);

        return new Contract(
            subject ?? string.Empty,
            type.ToDisplayString(),
            schema,
            context.TargetNode.GetLocation(),
            [.. builder.Unsupported.Select(u => (u.Member.Name, u.TypeName))]);
    }

    private static void Emit(
        SourceProductionContext context,
        (ImmutableArray<Contract> Contracts, ImmutableArray<(string Name, string Path, string? Text)> Files) input)
    {
        var (contracts, files) = input;

        if (contracts.IsDefaultOrEmpty)
        {
            return;
        }

        foreach (var duplicate in contracts
                     .Where(c => SubjectPattern.IsMatch(c.Subject))
                     .GroupBy(c => c.Subject, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DuplicateSubject,
                duplicate.First().Location,
                duplicate.Key,
                string.Join(", ", duplicate.Select(d => d.ClrType))));
        }

        var emitted = new StringBuilder("// <auto-generated/>\n#nullable enable\n\n");

        foreach (var contract in contracts)
        {
            if (!SubjectPattern.IsMatch(contract.Subject))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidSubject, contract.Location, contract.Subject));
                continue;
            }

            foreach (var (member, typeName) in contract.Unsupported)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedMemberType, contract.Location, member, typeName));
            }

            CheckDrift(context, contract, files);

            emitted.Append("[assembly: global::Concordat.Contracts.ConcordatGeneratedSchema(")
                .Append(Literal(contract.Subject)).Append(", ")
                .Append(Literal(contract.ClrType)).Append(", ")
                .Append(Literal(contract.Schema)).Append(")]\n");
        }

        context.AddSource("ConcordatGeneratedSchemas.g.cs", SourceText.From(emitted.ToString(), Encoding.UTF8));
    }

    private static void CheckDrift(
        SourceProductionContext context,
        Contract contract,
        ImmutableArray<(string Name, string Path, string? Text)> files)
    {
        var file = files.FirstOrDefault(f => string.Equals(f.Name, contract.Subject, StringComparison.Ordinal));

        if (file.Path is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                ContractMissing, contract.Location, contract.ClrType, $"contracts/{contract.Subject}.json"));
            return;
        }

        var difference = JsonComparer.FirstDifference(file.Text ?? string.Empty, contract.Schema);

        if (difference is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                ContractDrift, contract.Location, contract.ClrType, file.Path, difference));
        }
    }

    private static string Literal(string value)
    {
        var escaped = new StringBuilder("\"");

        foreach (var c in value)
        {
            switch (c)
            {
                case '"': escaped.Append("\\\""); break;
                case '\\': escaped.Append("\\\\"); break;
                case '\n': escaped.Append("\\n"); break;
                case '\r': escaped.Append("\\r"); break;
                default:
                    if (c < 0x20)
                    {
                        escaped.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        escaped.Append(c);
                    }

                    break;
            }
        }

        return escaped.Append('"').ToString();
    }

    private readonly record struct Contract(
        string Subject,
        string ClrType,
        string Schema,
        Location Location,
        ImmutableArray<(string Member, string TypeName)> Unsupported);
}
