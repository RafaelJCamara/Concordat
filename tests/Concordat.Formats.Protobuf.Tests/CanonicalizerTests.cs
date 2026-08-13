using Concordat.Domain.Registry;
using Concordat.Domain.Results;
using Concordat.Formats.Abstractions;
using Concordat.Formats.Protobuf;

namespace Concordat.Formats.Protobuf.Tests;

public class CanonicalizerTests
{
    private static readonly ProtoSchemaCanonicalizer Canonicalizer = new();

    private static string Canonical(string body)
    {
        var result = Canonicalizer.Canonicalize(body);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    private static SchemaId IdOf(string body) =>
        SchemaIdComputer.Compute(SchemaFormat.Protobuf, Canonical(body));

    [Fact]
    public void Handles_TheProtobufFormat() =>
        Assert.Equal(SchemaFormat.Protobuf, Canonicalizer.Format);

    [Fact]
    public void ASimpleMessage_RoundTrips() =>
        Assert.Equal(
            """
            syntax = "proto3";
            package acme.orders;
            message Order {
              string id = 1;
              int32 quantity = 2;
            }

            """.ReplaceLineEndings("\n"),
            Canonical(
                """
                syntax = "proto3";
                package acme.orders;
                // The order we ship.
                message Order {
                  string id = 1;  // its id
                  int32 quantity = 2;
                }
                """));

    [Fact]
    public void Comments_AreStripped() =>
        Assert.DoesNotContain(
            "shipped",
            Canonical(
                """
                syntax = "proto3";
                /* how it is shipped */
                message M { string a = 1; }
                """),
            StringComparison.Ordinal);

    [Fact]
    public void FieldsAreSortedByNumber_NotByDeclarationOrder() =>
        Assert.Equal(
            IdOf("""syntax = "proto3"; message M { string a = 1; string b = 2; }"""),
            IdOf("""syntax = "proto3"; message M { string b = 2; string a = 1; }"""));

    [Fact]
    public void ImportOrder_DoesNotAffectTheId() =>
        // DESIGN §12 asks for exactly this.
        Assert.Equal(
            IdOf(
                """
                syntax = "proto3";
                import "a.proto";
                import "b.proto";
                message M { string a = 1; }
                """),
            IdOf(
                """
                syntax = "proto3";
                import "b.proto";
                import "a.proto";
                message M { string a = 1; }
                """));

    [Fact]
    public void MessageDeclarationOrder_DoesNotAffectTheId() =>
        Assert.Equal(
            IdOf("""syntax = "proto3"; message A { string x = 1; } message B { string y = 1; }"""),
            IdOf("""syntax = "proto3"; message B { string y = 1; } message A { string x = 1; }"""));

    [Fact]
    public void WhitespaceAndComments_DoNotAffectTheId() =>
        Assert.Equal(
            IdOf("""syntax = "proto3"; message M { string a = 1; }"""),
            IdOf(
                """
                syntax   =   "proto3";

                // a message
                message M {

                    string a = 1;   /* the field */

                }
                """));

    [Fact]
    public void ReservedRanges_AreMergedAndSorted() =>
        // 'reserved 3, 2;' and 'reserved 2 to 3;' reserve the same numbers.
        Assert.Equal(
            IdOf("""syntax = "proto3"; message M { reserved 3, 2; string a = 1; }"""),
            IdOf("""syntax = "proto3"; message M { reserved 2 to 3; string a = 1; }"""));

    [Fact]
    public void TypeReferences_ResolveToFullnames() =>
        // 'Item', 'Order.Item' and the leading-dot fullname all name one type.
        Assert.Equal(
            IdOf(
                """
                syntax = "proto3";
                package acme;
                message Order {
                  message Item { string sku = 1; }
                  Item item = 1;
                }
                """),
            IdOf(
                """
                syntax = "proto3";
                package acme;
                message Order {
                  message Item { string sku = 1; }
                  .acme.Order.Item item = 1;
                }
                """));

    [Fact]
    public void AForwardReferenceToASiblingMessage_Resolves() =>
        Assert.Contains(
            ".acme.Item item = 1;",
            Canonical(
                """
                syntax = "proto3";
                package acme;
                message Order { Item item = 1; }
                message Item { string sku = 1; }
                """),
            StringComparison.Ordinal);

    [Fact]
    public void AnImportedTypeReference_IsLeftAsWritten() =>
        // Resolving it needs the imported file, which is DECISIONS-PENDING #16.
        Assert.Contains(
            "common.Address address = 1;",
            Canonical(
                """
                syntax = "proto3";
                package acme;
                import "common.proto";
                message Order { common.Address address = 1; }
                """),
            StringComparison.Ordinal);

    [Fact]
    public void OneofsAreKept_AndTheirFieldsSorted() =>
        Assert.Contains(
            """
              oneof payment {
                string card = 2;
                string cash = 3;
              }
            """.ReplaceLineEndings("\n"),
            Canonical(
                """
                syntax = "proto3";
                message M {
                  string id = 1;
                  oneof payment { string cash = 3; string card = 2; }
                }
                """),
            StringComparison.Ordinal);

    [Fact]
    public void MapsAreKept() =>
        Assert.Contains(
            "map<string, int32> counts = 1;",
            Canonical("""syntax = "proto3"; message M { map<string, int32> counts = 1; }"""),
            StringComparison.Ordinal);

    [Fact]
    public void EnumValues_AreSortedByNumber() =>
        Assert.Equal(
            IdOf("""syntax = "proto3"; enum E { A = 0; B = 1; }"""),
            IdOf("""syntax = "proto3"; enum E { B = 1; A = 0; }"""));

    [Fact]
    public void Options_AreKeptAndSorted() =>
        Assert.Contains(
            "option java_package = \"com.acme\";",
            Canonical(
                """
                syntax = "proto3";
                option java_package = "com.acme";
                message M { string a = 1; }
                """),
            StringComparison.Ordinal);

    [Fact]
    public void FieldOptions_AreKept() =>
        Assert.Contains(
            "[deprecated = true]",
            Canonical("""syntax = "proto3"; message M { string a = 1 [deprecated = true]; }"""),
            StringComparison.Ordinal);

    [Fact]
    public void Canonicalisation_IsIdempotent()
    {
        var once = Canonical(
            """
            syntax = "proto3";
            package acme;
            import "b.proto";
            import "a.proto";
            message Order {
              reserved 9, 7 to 8;
              oneof pay { string card = 3; }
              string id = 1;
              message Item { string sku = 1; }
              Item item = 2;
              enum Status { UNKNOWN = 0; }
            }
            """);

        Assert.Equal(once, Canonical(once));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyBody_IsRejected(string? body)
    {
        var result = Canonicalizer.Canonicalize(body);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SchemaBodyEmpty, result.Error!.Code);
    }

    [Theory]
    [InlineData("this is not proto")]
    [InlineData("""message M { string a = 1; }""")]
    [InlineData("""syntax = "proto2"; message M { }""")]
    [InlineData("""syntax = "proto3"; message M { required string a = 1; }""")]
    [InlineData("""syntax = "proto3"; message M { string a = 0; }""")]
    [InlineData("""syntax = "proto3"; service S { }""")]
    [InlineData("""syntax = "proto3"; message M { string a = 1; """)]
    [InlineData("""syntax = "proto3"; message M { /* unterminated """)]
    public void MalformedOrUnsupported_IsRejected(string body)
    {
        var result = Canonicalizer.Canonicalize(body);

        Assert.True(result.IsFailure);
        Assert.Equal(ConcordatCodes.SchemaMalformed, result.Error!.Code);
    }

    [Fact]
    public void Proto2_IsRefusedWithAReasonRatherThanMisparsed()
    {
        var result = Canonicalizer.Canonicalize("""syntax = "proto2"; message M { }""");

        Assert.True(result.IsFailure);
        Assert.Contains("proto3", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingSyntaxLine_IsRefused()
    {
        // Without the declaration protoc assumes proto2, so this is not "proto3 that forgot".
        var result = Canonicalizer.Canonicalize("""message M { string a = 1; }""");

        Assert.True(result.IsFailure);
        Assert.Contains("proto2", result.Error!.Message, StringComparison.Ordinal);
    }
}
