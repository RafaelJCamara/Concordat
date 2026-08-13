using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Concordat.Contracts.Tests;

/// <summary>A checked-in contract file, as the compiler would hand it to the generator.</summary>
internal sealed class FakeAdditionalText(string path, string text) : AdditionalText
{
    public override string Path { get; } = path;

    public override SourceText GetText(CancellationToken cancellationToken = default) =>
        SourceText.From(text);
}

/// <summary>What one generator run produced.</summary>
/// <param name="Diagnostics">Everything reported.</param>
/// <param name="GeneratedSource">The emitted assembly attributes.</param>
internal sealed record GeneratorRun(ImmutableArray<Diagnostic> Diagnostics, string GeneratedSource)
{
    public IEnumerable<Diagnostic> OfId(string id) =>
        Diagnostics.Where(d => d.Id == id);

    public Diagnostic Single(string id) => Assert.Single(Diagnostics, d => d.Id == id);

    /// <summary>The schema emitted for a subject, pulled back out of the generated attribute.</summary>
    public string SchemaFor(string subject)
    {
        var marker = $"\"{subject}\", ";
        var start = GeneratedSource.IndexOf(marker, StringComparison.Ordinal);

        Assert.True(start >= 0, $"no schema was emitted for '{subject}'.\n{GeneratedSource}");

        // subject, clrType, schema — skip past the CLR type to the third literal.
        var cursor = start + marker.Length;
        cursor = GeneratedSource.IndexOf(", ", cursor, StringComparison.Ordinal) + 2;

        var end = cursor;
        while (end < GeneratedSource.Length)
        {
            if (GeneratedSource[end] == '\\')
            {
                end += 2;
                continue;
            }

            if (GeneratedSource[end] == '"' && end > cursor)
            {
                break;
            }

            end++;
        }

        return System.Text.RegularExpressions.Regex.Unescape(
            GeneratedSource.Substring(cursor + 1, end - cursor - 1));
    }
}

/// <summary>
/// Runs the generator over source text, in memory.
/// </summary>
/// <remarks>
/// A real compilation rather than a hand-built symbol graph, because the mapping reads
/// nullability annotations, <c>required</c> members and interface implementations — all of
/// which the compiler computes and a stub would have to fake, wrongly.
/// </remarks>
internal static class GeneratorHarness
{
    private const string AttributeSource = """
        namespace Concordat.Contracts
        {
            [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct)]
            public sealed class ConcordatContractAttribute : System.Attribute
            {
                public ConcordatContractAttribute(string subject) => Subject = subject;
                public string Subject { get; }
                public string? Description { get; set; }
            }

            [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
            public sealed class ConcordatGeneratedSchemaAttribute : System.Attribute
            {
                public ConcordatGeneratedSchemaAttribute(string subject, string clrType, string schema) { }
            }
        }
        """;

    public static GeneratorRun Run(string source, params (string Path, string Text)[] contracts)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        var compilation = CSharpCompilation.Create(
            "ContractsUnderTest",
            [
                CSharpSyntaxTree.ParseText(AttributeSource),
                CSharpSyntaxTree.ParseText(source),
            ],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver
            .Create(new Generators.ContractGenerator())
            .AddAdditionalTexts([.. contracts.Select(c => (AdditionalText)new FakeAdditionalText(c.Path, c.Text))]);

        var result = driver.RunGenerators(compilation).GetRunResult();

        var generated = string.Join(
            "\n", result.GeneratedTrees.Select(t => t.GetText().ToString()));

        return new GeneratorRun(result.Diagnostics, generated);
    }

    /// <summary>Runs with a contract file whose content is the schema the type produces.</summary>
    public static GeneratorRun RunMatching(string source, string subject)
    {
        var first = Run(source);
        return Run(source, ($"contracts/{subject}.json", first.SchemaFor(subject)));
    }
}
