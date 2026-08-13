using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;

namespace Concordat.Application.Tests;

/// <summary>
/// The per-format lookup that keeps the Application layer from referencing any concrete
/// format.
/// </summary>
/// <remarks>
/// Every member here resolves by <see cref="SchemaFormat"/> out of an unordered sequence, so
/// registration order must not decide the answer and a missing implementation must fail loudly.
/// A silent fallback to the first registered implementation would canonicalise an Avro document
/// with the JSON canonicaliser and produce a verdict that looks entirely normal.
/// </remarks>
public class FormatRegistryTests
{
    private sealed class Canonicalizer(SchemaFormat format) : ISchemaCanonicalizer
    {
        public SchemaFormat Format => format;

        public Result<string> Canonicalize(string? body) => Result<string>.Success(format.ToString());
    }

    private sealed class Checker(SchemaFormat format) : ICompatibilityChecker
    {
        public SchemaFormat Format => format;

        public CompatibilityReport Check(
            string proposedCanonicalBody,
            IReadOnlyList<PriorSchema> priors,
            CompatibilityPolicy policy,
            ContentModel contentModel) =>
            new(true, [], [], SemverBump.None);
    }

    private sealed class Extractor(SchemaFormat format) : ISchemaReferenceExtractor
    {
        public SchemaFormat Format => format;

        public Result<IReadOnlyList<Reference>> Extract(string canonicalBody) =>
            Result<IReadOnlyList<Reference>>.Success([]);
    }

    private sealed class Portability(SchemaFormat format) : ISchemaPortabilityChecker
    {
        public SchemaFormat Format => format;

        public IReadOnlyList<PortabilityFinding> Check(string canonicalBody) => [];
    }

    private sealed class Bundler(SchemaFormat format) : ISchemaBundler
    {
        public SchemaFormat Format => format;

        public Result<string> Bundle(
            string canonicalBody, IReadOnlyDictionary<string, string> resolved) =>
            Result<string>.Success(canonicalBody);
    }

    private static SchemaFormatRegistry Registry() =>
        new(
            [new Canonicalizer(SchemaFormat.Json), new Canonicalizer(SchemaFormat.Avro)],
            [new Checker(SchemaFormat.Json), new Checker(SchemaFormat.Avro)],
            [new Extractor(SchemaFormat.Json), new Extractor(SchemaFormat.Avro)],
            [new Portability(SchemaFormat.Json)]);

    [Fact]
    public void EachServiceIsResolvedByFormatRatherThanByRegistrationOrder()
    {
        var registry = Registry();

        // Avro is registered second in every sequence, so a first-match implementation would
        // hand back the JSON one and nothing about the result would look wrong.
        Assert.Equal(SchemaFormat.Avro, registry.Canonicalizer(SchemaFormat.Avro).Format);
        Assert.Equal(SchemaFormat.Avro, registry.Checker(SchemaFormat.Avro).Format);
        Assert.Equal(SchemaFormat.Avro, registry.ReferenceExtractor(SchemaFormat.Avro).Format);
    }

    [Fact]
    public void AnUnregisteredFormat_ThrowsRatherThanFallingBackToJson()
    {
        var registry = Registry();

        Assert.Throws<NotSupportedException>(() => registry.Canonicalizer(SchemaFormat.Protobuf));
        Assert.Throws<NotSupportedException>(() => registry.Checker(SchemaFormat.Protobuf));
        Assert.Throws<NotSupportedException>(() => registry.ReferenceExtractor(SchemaFormat.Protobuf));
    }

    [Fact]
    public void TheRefusalNamesTheFormatThatWasAskedFor()
    {
        // "No canonicaliser registered" alone sends whoever reads the log looking at the wrong
        // composition root.
        var error = Assert.Throws<NotSupportedException>(
            () => Registry().Checker(SchemaFormat.Protobuf));

        Assert.Contains("Protobuf", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFormatWithNoPortabilityChecker_AnswersNullInsteadOfThrowing()
    {
        // Not a gap: a format with no modelled cross-implementation divergence has nothing to
        // report. JSON Schema is the one with five independent validators reading the same
        // text, so it is the one with a checker.
        var registry = Registry();

        Assert.NotNull(registry.PortabilityChecker(SchemaFormat.Json));
        Assert.Null(registry.PortabilityChecker(SchemaFormat.Avro));
    }

    [Fact]
    public void TheBundlerRegistryResolvesByFormatAndRefusesTheRest()
    {
        var bundlers = new SchemaBundlerRegistry(
            [new Bundler(SchemaFormat.Json), new Bundler(SchemaFormat.Avro)]);

        Assert.Equal(SchemaFormat.Avro, bundlers.Bundler(SchemaFormat.Avro).Format);
        Assert.Throws<NotSupportedException>(() => bundlers.Bundler(SchemaFormat.Protobuf));
    }
}
