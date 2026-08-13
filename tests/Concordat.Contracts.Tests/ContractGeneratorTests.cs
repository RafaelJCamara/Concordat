namespace Concordat.Contracts.Tests;

/// <summary>
/// The build-time drift check — M3's exit criterion.
/// </summary>
/// <remarks>
/// "A breaking change to a C# record fails the build locally, naming the exact path." These
/// tests are that sentence, executed.
/// </remarks>
public class ContractGeneratorTests
{
    private const string Order = """
        using Concordat.Contracts;

        [ConcordatContract("acme.orders.OrderCreated")]
        public record OrderCreated(int Id, string Reference, string? Note);
        """;

    [Fact]
    public void TheSchemaFollowsTheTypesNullability()
    {
        // Nullability is the contract. A second annotation for requiredness would be one more
        // thing to keep in sync, and it would fall out of sync immediately.
        var run = GeneratorHarness.Run(Order);

        Assert.Equal(
            """
            {"type":"object","properties":{"id":{"type":"integer"},"note":{"type":["string","null"]},"reference":{"type":"string"}},"required":["id","reference"]}
            """,
            run.SchemaFor("acme.orders.OrderCreated"));
    }

    [Fact]
    public void PropertiesAreSortedSoTheOutputIsStable()
    {
        // Member order in C# is source order. Without sorting, moving a property up the file
        // would register as a schema change and fail the build for no reason.
        var reordered = GeneratorHarness.Run("""
            using Concordat.Contracts;

            [ConcordatContract("acme.orders.OrderCreated")]
            public record OrderCreated(string? Note, string Reference, int Id);
            """);

        Assert.Equal(
            GeneratorHarness.Run(Order).SchemaFor("acme.orders.OrderCreated"),
            reordered.SchemaFor("acme.orders.OrderCreated"));
    }

    [Fact]
    public void AMatchingContractProducesNoDiagnostics()
    {
        var run = GeneratorHarness.RunMatching(Order, "acme.orders.OrderCreated");

        Assert.Empty(run.Diagnostics);
    }

    [Fact]
    public void FormattingAndKeyOrderInTheCheckedInFileAreNotDrift()
    {
        // The comparison is structural, not textual. Byte comparison would make this generator
        // and the CLI's canonicaliser two implementations of one format that must agree
        // exactly — which is the divergence this project exists to prevent.
        var run = GeneratorHarness.Run(
            Order,
            ("contracts/acme.orders.OrderCreated.json", """
                {
                  "required": [ "reference", "id" ],
                  "properties": {
                    "reference": { "type": "string" },
                    "note":      { "type": [ "string", "null" ] },
                    "id":        { "type": "integer" }
                  },
                  "type": "object"
                }
                """));

        Assert.Empty(run.Diagnostics);
    }

    [Fact]
    public void MakingAnOptionalMemberRequiredIsDriftAndSaysWhich()
    {
        // The headline case: a developer removes `?` and the build fails, in their editor,
        // naming the member — rather than a consumer finding out from a quarantined message.
        var run = GeneratorHarness.Run(
            """
            using Concordat.Contracts;

            [ConcordatContract("acme.orders.OrderCreated")]
            public record OrderCreated(int Id, string Reference, string Note);
            """,
            ("contracts/acme.orders.OrderCreated.json",
             GeneratorHarness.Run(Order).SchemaFor("acme.orders.OrderCreated")));

        var drift = run.Single("CDT003");

        Assert.Equal(DiagnosticSeverity.Error, drift.Severity);

        // Reported at the member whose nullability changed, not at #/required — the property
        // is compared first and is the more actionable of the two. The message shows both
        // values, because "the file has Array where the type produces String" would be
        // accurate and useless, while ["string","null"] versus "string" says: you removed a ?.
        var message = drift.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains("#/properties/note/type", message, StringComparison.Ordinal);
        Assert.Contains("""["string","null"]""", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddingARequiredMemberIsReportedAsARequirednessChange()
    {
        // The `required` set is compared as a set, so this is the path that exercises the
        // set-difference message rather than a per-property type change.
        var run = GeneratorHarness.Run(
            """
            using Concordat.Contracts;

            [ConcordatContract("acme.orders.OrderCreated")]
            public record OrderCreated(int Id, string Reference, string? Note, int Quantity);
            """,
            ("contracts/acme.orders.OrderCreated.json",
             GeneratorHarness.Run(Order).SchemaFor("acme.orders.OrderCreated")));

        var message = run.Single("CDT003").GetMessage(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Contains("quantity", message, StringComparison.Ordinal);
    }

    [Fact]
    public void RenamingAPropertyIsDriftAndNamesBothSides()
    {
        var run = GeneratorHarness.Run(
            """
            using Concordat.Contracts;

            [ConcordatContract("acme.orders.OrderCreated")]
            public record OrderCreated(int Id, string Ref, string? Note);
            """,
            ("contracts/acme.orders.OrderCreated.json",
             GeneratorHarness.Run(Order).SchemaFor("acme.orders.OrderCreated")));

        var message = run.Single("CDT003").GetMessage(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Contains("#/properties", message, StringComparison.Ordinal);
        Assert.Contains("'ref'", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangingATypeIsDriftAndShowsBothTypes()
    {
        var run = GeneratorHarness.Run(
            """
            using Concordat.Contracts;

            [ConcordatContract("acme.orders.OrderCreated")]
            public record OrderCreated(string Id, string Reference, string? Note);
            """,
            ("contracts/acme.orders.OrderCreated.json",
             GeneratorHarness.Run(Order).SchemaFor("acme.orders.OrderCreated")));

        var message = run.Single("CDT003").GetMessage(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Contains("#/properties/id/type", message, StringComparison.Ordinal);
        Assert.Contains("integer", message, StringComparison.Ordinal);
        Assert.Contains("string", message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoContractFileIsAWarningNotSilence()
    {
        // A drift check with nothing to check against passes vacuously, which is the worst
        // possible state: the build is green and the contract is unguarded.
        var run = GeneratorHarness.Run(Order);
        var missing = run.Single("CDT004");

        Assert.Equal(DiagnosticSeverity.Warning, missing.Severity);
        Assert.Contains(
            "contracts/acme.orders.OrderCreated.json",
            missing.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidSubjectIsRefusedWithTheGrammar()
    {
        var run = GeneratorHarness.Run("""
            using Concordat.Contracts;

            [ConcordatContract("acme.orders.Order-Created")]
            public record OrderCreated(int Id);
            """);

        Assert.Equal(DiagnosticSeverity.Error, run.Single("CDT001").Severity);
    }

    [Fact]
    public void TwoTypesClaimingOneSubjectIsAnError()
    {
        // Which schema won would depend on compilation order.
        var run = GeneratorHarness.Run("""
            using Concordat.Contracts;

            [ConcordatContract("acme.orders.OrderCreated")]
            public record One(int Id);

            [ConcordatContract("acme.orders.OrderCreated")]
            public record Two(string Name);
            """);

        Assert.Equal(DiagnosticSeverity.Error, run.Single("CDT005").Severity);
    }

    [Fact]
    public void EnumsBecomeStringEnumsSortedByName()
    {
        // Names, not ordinals: reordering an enum silently changes every numeric value, and
        // the wire should not depend on declaration order.
        var run = GeneratorHarness.Run("""
            using Concordat.Contracts;

            public enum Status { Placed, Shipped, Cancelled }

            [ConcordatContract("acme.orders.WithEnum")]
            public record WithEnum(Status Status);
            """);

        Assert.Contains(
            "\"enum\":[\"Cancelled\",\"Placed\",\"Shipped\"]",
            run.SchemaFor("acme.orders.WithEnum"),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("System.Guid", "\"format\":\"uuid\"")]
    [InlineData("System.DateTimeOffset", "\"format\":\"date-time\"")]
    [InlineData("System.DateOnly", "\"format\":\"date\"")]
    [InlineData("System.Uri", "\"format\":\"uri\"")]
    public void WellKnownTypesCarryTheirFormat(string clrType, string expected)
    {
        var run = GeneratorHarness.Run($$"""
            using Concordat.Contracts;

            [ConcordatContract("acme.orders.Formats")]
            public record Formats({{clrType}} Value);
            """);

        Assert.Contains(expected, run.SchemaFor("acme.orders.Formats"), StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionsAndDictionariesAreMapped()
    {
        var run = GeneratorHarness.Run("""
            using System.Collections.Generic;
            using Concordat.Contracts;

            [ConcordatContract("acme.orders.Shapes")]
            public record Shapes(
                IReadOnlyList<string> Tags,
                int[] Codes,
                IDictionary<string, int> Counts);
            """);

        var schema = run.SchemaFor("acme.orders.Shapes");

        Assert.Contains("\"codes\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}", schema, StringComparison.Ordinal);

        Assert.Contains("\"counts\":{\"type\":\"object\",\"additionalProperties\":{\"type\":\"integer\"}}", schema, StringComparison.Ordinal);

        Assert.Contains("\"tags\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedTypesAreExpandedInPlace()
    {
        var run = GeneratorHarness.Run("""
            using Concordat.Contracts;

            public record Customer(int Id, string? Name);

            [ConcordatContract("acme.orders.Nested")]
            public record Nested(Customer Customer);
            """);

        Assert.Contains(
            "\"customer\":{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\"},\"name\":{\"type\":[\"string\",\"null\"]}},\"required\":[\"id\"]}",
            run.SchemaFor("acme.orders.Nested"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ASelfReferencingTypeTerminatesRatherThanRecursing()
    {
        // Emitting an unconstrained object is honest. Inventing a $ref would commit every
        // other SDK to resolving it identically (ADR-019).
        var run = GeneratorHarness.Run("""
            using Concordat.Contracts;

            [ConcordatContract("acme.orders.Tree")]
            public record Tree(int Id, Tree? Child);
            """);

        Assert.Contains("acme.orders.Tree", run.GeneratedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CDT", string.Join(",", run.Diagnostics.Select(d => d.Id).Where(i => i != "CDT004")));
    }

    [Fact]
    public void AnUnparsableContractFileSaysSoRatherThanReportingDrift()
    {
        var run = GeneratorHarness.Run(Order, ("contracts/acme.orders.OrderCreated.json", "{ not json"));

        Assert.Contains(
            "could not be parsed",
            run.Single("CDT003").GetMessage(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheGeneratedSchemaIsAcceptedByTheRealCanonicalizer()
    {
        // The generator emits its own JSON by hand, so it has to survive the pipeline the rest
        // of the product runs. A schema the registry would refuse is worse than no generator.
        var schema = GeneratorHarness.Run(Order).SchemaFor("acme.orders.OrderCreated");

        var canonical = new Concordat.Formats.Json.JsonSchemaCanonicalizer().Canonicalize(schema);

        Assert.True(canonical.IsSuccess, schema);
    }

    [Fact]
    public void TheGeneratedSchemaValidatesAMatchingPayload()
    {
        var schema = GeneratorHarness.Run(Order).SchemaFor("acme.orders.OrderCreated");
        var canonical = new Concordat.Formats.Json.JsonSchemaCanonicalizer().Canonicalize(schema);

        var result = new Concordat.Formats.Json.NJsonSchemaPayloadValidator()
            .Validate(canonical.Value, """{"id":1,"reference":"abc"}""");

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Message)));
    }
}
