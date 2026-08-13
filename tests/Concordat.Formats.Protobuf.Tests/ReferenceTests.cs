using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Protobuf;

namespace Concordat.Formats.Protobuf.Tests;

/// <summary>ADR-023: v1 refuses cross-subject Protobuf imports rather than guessing a version.</summary>
public class ReferenceTests
{
    private static readonly ProtoSchemaCanonicalizer Canonicalizer = new();
    private static readonly ProtoSchemaReferenceExtractor Extractor = new();
    private static readonly ProtoSchemaBundler Bundler = new();

    private static string Canonical(string body)
    {
        var result = Canonicalizer.Canonicalize(body);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    [Fact]
    public void Handles_TheProtobufFormat()
    {
        Assert.Equal(SchemaFormat.Protobuf, Extractor.Format);
        Assert.Equal(SchemaFormat.Protobuf, Bundler.Format);
    }

    [Fact]
    public void ASelfContainedSchema_HasNoReferences()
    {
        var result = Extractor.Extract(Canonical(
            """
            syntax = "proto3";
            package acme;
            message Order {
              string id = 1;
              message Item { string sku = 1; }
              Item item = 2;
            }
            """));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(result.Value);
    }

    [Fact]
    public void AnImportOfAnotherSubject_IsRefused()
    {
        var result = Extractor.Extract(Canonical(
            """
            syntax = "proto3";
            package acme;
            import "acme/common.proto";
            message Order { string id = 1; }
            """));

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SchemaReferencesUnsupported, result.Error!.Code);
        Assert.Contains("acme/common.proto", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("google/protobuf/timestamp.proto")]
    [InlineData("google/protobuf/duration.proto")]
    [InlineData("google/protobuf/any.proto")]
    [InlineData("google/protobuf/struct.proto")]
    [InlineData("google/protobuf/wrappers.proto")]
    public void AWellKnownTypeImport_IsAllowed(string import)
    {
        // These ship with every Protobuf runtime and are resolved by the compiler, not by a
        // registry. Refusing them would rule out most real schemas for no correctness gain.
        var result = Extractor.Extract(Canonical(
            $$"""
              syntax = "proto3";
              package acme;
              import "{{import}}";
              message Order { string id = 1; }
              """));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(result.Value);
    }

    [Fact]
    public void AWellKnownTypeCanBeUsed_WhileAnotherSubjectsImportIsStillRefused()
    {
        var result = Extractor.Extract(Canonical(
            """
            syntax = "proto3";
            package acme;
            import "google/protobuf/timestamp.proto";
            import "acme/common.proto";
            message Order { google.protobuf.Timestamp at = 1; }
            """));

        Assert.True(result.IsFailure);
        Assert.Contains("acme/common.proto", result.Error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("timestamp.proto", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusal_NamesEveryOffendingImport()
    {
        var result = Extractor.Extract(Canonical(
            """
            syntax = "proto3";
            import "a.proto";
            import "b.proto";
            message Order { string id = 1; }
            """));

        Assert.Contains("a.proto", result.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("b.proto", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWellKnownTypeFieldSurvivesCanonicalisation() =>
        Assert.Contains(
            "google.protobuf.Timestamp at = 1;",
            Canonical(
                """
                syntax = "proto3";
                package acme;
                import "google/protobuf/timestamp.proto";
                message Order { google.protobuf.Timestamp at = 1; }
                """),
            StringComparison.Ordinal);

    [Fact]
    public void EmptyBody_IsRejected()
    {
        Assert.Equal(ConcordatCodes.SchemaBodyEmpty, Extractor.Extract("").Error!.Code);
        Assert.Equal(
            ConcordatCodes.SchemaBodyEmpty,
            Bundler.Bundle("", new Dictionary<string, string>()).Error!.Code);
    }

    [Fact]
    public void Bundling_ReturnsTheDocumentUnchanged()
    {
        var canonical = Canonical("""syntax = "proto3"; message Order { string id = 1; }""");

        var result = Bundler.Bundle(canonical, new Dictionary<string, string>());

        Assert.True(result.IsSuccess);
        Assert.Equal(canonical, result.Value);
    }
}
