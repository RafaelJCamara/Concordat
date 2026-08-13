using System.Text;
using Concordat.Domain.Messaging;
using Concordat.Domain.Registry;
using Concordat.Domain.Results;

namespace Concordat.Domain.Tests;

public class EnvelopeWriterTests
{
    private static SchemaId Id() => SchemaId.Create(new string('a', 32)).Value;

    [Fact]
    public void AMinimalEnvelopeCarriesOnlyVersionAndSchemaId()
    {
        var headers = EnvelopeWriter.Headers(Id());

        Assert.Equal(2, headers.Count);
        Assert.Equal("1", headers["concordat-v"]);
        Assert.Equal(new string('a', 32), headers["concordat-schema-id"]);
    }

    [Fact]
    public void AbsentOptionalsAreOmittedNotWrittenEmpty()
    {
        // The reader treats a present-but-empty header as malformed, so writing one would turn
        // a perfectly good message into a quarantined one.
        var headers = EnvelopeWriter.Headers(Id(), subject: null, ordinal: null, semver: null);

        Assert.DoesNotContain("concordat-subject", headers.Keys);
        Assert.DoesNotContain("concordat-version", headers.Keys);
        Assert.DoesNotContain("concordat-semver", headers.Keys);
    }

    [Fact]
    public void AFullEnvelopeRoundTrips()
    {
        var headers = EnvelopeWriter.Headers(
            Id(),
            SubjectName.Create("acme.orders.OrderCreated").Value,
            3,
            SemanticVersion.Create("2.1.0").Value,
            SchemaFormat.Json);

        var result = EnvelopeReader.Read(headers.ToDictionary(h => h.Key, h => (object?)h.Value));

        Assert.True(result.IsEnveloped);
        var envelope = result.Envelope!;
        Assert.Equal("acme.orders.OrderCreated", envelope.Subject!.Value);
        Assert.Equal(3, envelope.Ordinal);
        Assert.Equal(new SemanticVersion(2, 1, 0), envelope.Semver);
        Assert.Equal(SchemaFormat.Json, envelope.Format);
        Assert.Empty(envelope.Warnings);
    }

    [Fact]
    public void TheOrdinalIsWrittenInInvariantCulture()
    {
        // A host under a locale with digit grouping would otherwise emit a value no other SDK
        // can parse.
        var headers = EnvelopeWriter.Headers(Id(), ordinal: 1234567);

        Assert.Equal("1234567", headers["concordat-version"]);
    }

    [Fact]
    public void ContentTypeEnvelopeRoundTrips()
    {
        var contentType = ContentTypeEnvelope.Format(SchemaFormat.Json, Id());

        Assert.Equal($"application/json+concordat.v1.{new string('a', 32)}", contentType);
        Assert.True(ContentTypeEnvelope.TryParse(contentType, out var id, out var format));
        Assert.Equal(Id(), id);
        Assert.Equal(SchemaFormat.Json, format);
    }
}

public class EnvelopeReaderTests
{
    private const string ValidId = "7f3a9c2ea1b84d5c9e07f2b3c4d5e6b4";

    private static Dictionary<string, object?> Minimal() => new(StringComparer.Ordinal)
    {
        ["concordat-v"] = "1",
        ["concordat-schema-id"] = ValidId,
    };

    [Fact]
    public void NoHeaders_IsNotEnvelopedRatherThanAnError()
    {
        // The brownfield path. Mode A exists so a consumer without a Concordat client still
        // reads plain JSON; treating an un-enveloped message as an error would break that.
        Assert.Equal(EnvelopeKind.None, EnvelopeReader.Read(null).Kind);
        Assert.False(EnvelopeReader.Read(new Dictionary<string, object?>()).IsMalformed);
    }

    [Fact]
    public void OtherLibrariesHeadersAloneAreNotAnEnvelope()
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["MT-Activity-Id"] = "x",
            ["rbs2-msg-type"] = "y",
        };

        Assert.Equal(EnvelopeKind.None, EnvelopeReader.Read(headers).Kind);
    }

    [Fact]
    public void ByteArrayValuesAreDecoded()
    {
        // What RabbitMQ.Client hands back for a value it wrote as a string. By design and
        // permanent, per ADR-010.
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["concordat-v"] = "1"u8.ToArray(),
            ["concordat-schema-id"] = Encoding.UTF8.GetBytes(ValidId),
        };

        var result = EnvelopeReader.Read(headers);

        Assert.True(result.IsEnveloped);
        Assert.Equal(ValidId, result.Envelope!.SchemaId.Value);
    }

    [Fact]
    public void InvalidUtf8OnAnAdvisoryHeader_WarnsAndIsNotSubstituted()
    {
        // Encoding.UTF8 would substitute U+FFFD and turn a corrupt value into a
        // valid-looking wrong one. It must also not pass silently: an unreadable subject
        // would otherwise look identical to an absent one.
        var headers = Minimal();
        headers["concordat-subject"] = new byte[] { 0xC3, 0x28 };

        var result = EnvelopeReader.Read(headers);

        Assert.True(result.IsEnveloped);
        Assert.Null(result.Envelope!.Subject);
        Assert.Contains(
            result.Envelope.Warnings,
            w => w.Code == ConcordatCodes.EnvelopeHeaderEncodingInvalid);
    }

    [Fact]
    public void AnInvalidUtf8SchemaId_IsMalformed()
    {
        var headers = Minimal();
        headers["concordat-schema-id"] = new byte[] { 0xFF, 0xFE, 0xFD };

        var result = EnvelopeReader.Read(headers);

        Assert.True(result.IsMalformed);
        Assert.Equal(ConcordatCodes.EnvelopeHeaderEncodingInvalid, result.Error!.Code);
    }

    [Fact]
    public void ANonStringHeaderValue_IsRejectedNotStringified()
    {
        // ToString() on an int would produce a plausible-looking value that is silently wrong.
        var headers = Minimal();
        headers["concordat-schema-id"] = 42;

        var result = EnvelopeReader.Read(headers);

        Assert.True(result.IsMalformed);
        Assert.Equal(ConcordatCodes.EnvelopeHeaderTypeInvalid, result.Error!.Code);
    }

    [Fact]
    public void AnUnsupportedEnvelopeVersion_StopsInterpretationEntirely()
    {
        // A v2 producer may have redefined the other headers, so guessing is worse than
        // declining.
        var headers = Minimal();
        headers["concordat-v"] = "2";

        var result = EnvelopeReader.Read(headers);

        Assert.True(result.IsMalformed);
        Assert.Equal(ConcordatCodes.EnvelopeVersionUnsupported, result.Error!.Code);
    }

    [Fact]
    public void AVersionWithNoSchemaId_IsMalformed()
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal) { ["concordat-v"] = "1" };

        var result = EnvelopeReader.Read(headers);

        Assert.True(result.IsMalformed);
        Assert.Equal(ConcordatCodes.EnvelopeSchemaIdMissing, result.Error!.Code);
    }

    [Fact]
    public void APresentButEmptyHeader_IsMalformedNotAbsent()
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["concordat-v"] = string.Empty,
        };

        Assert.True(EnvelopeReader.Read(headers).IsMalformed);
    }

    [Fact]
    public void HeaderLookupIsCaseSensitive()
    {
        // A case-folding lookup would accept "Concordat-V" from one SDK and not another.
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Concordat-V"] = "1",
            ["Concordat-Schema-Id"] = ValidId,
        };

        Assert.Equal(EnvelopeKind.None, EnvelopeReader.Read(headers).Kind);
    }

    [Fact]
    public void ValuesAreNotTrimmed()
    {
        // " acme.A" and "acme.A" must not become two spellings of one wire value. SubjectName
        // trims on creation, so the reader must not hand it something to trim.
        var headers = Minimal();
        headers["concordat-subject"] = "  acme.Order  ";

        var result = EnvelopeReader.Read(headers);

        Assert.True(result.IsEnveloped);
        Assert.Null(result.Envelope!.Subject);
        Assert.Contains(
            result.Envelope.Warnings, w => w.Code == ConcordatCodes.SubjectNameInvalid);
    }

    [Fact]
    public void AMalformedOrdinal_WarnsRatherThanRejects()
    {
        // The schema id already pins the schema, so the ordinal tells us nothing new.
        var headers = Minimal();
        headers["concordat-version"] = "not-a-number";

        var result = EnvelopeReader.Read(headers);

        Assert.True(result.IsEnveloped);
        Assert.Null(result.Envelope!.Ordinal);
        Assert.Contains(
            result.Envelope.Warnings, w => w.Code == ConcordatCodes.EnvelopeOrdinalMalformed);
    }

    [Fact]
    public void AMalformedSemver_WarnsRatherThanRejects()
    {
        // Quarantining a structurally valid payload over a human label would be a
        // self-inflicted outage.
        var headers = Minimal();
        headers["concordat-semver"] = "2.0.0-rc.1";

        var result = EnvelopeReader.Read(headers);

        Assert.True(result.IsEnveloped);
        Assert.Null(result.Envelope!.Semver);
        Assert.NotEmpty(result.Envelope.Warnings);
    }

    [Fact]
    public void AnUnknownFormat_Rejects()
    {
        // Unlike the advisory fields: an unknown format means the client cannot know how to
        // validate, so proceeding would be a guess.
        var headers = Minimal();
        headers["concordat-format"] = "yaml";

        var result = EnvelopeReader.Read(headers);

        Assert.True(result.IsMalformed);
        Assert.Equal(ConcordatCodes.EnvelopeFormatUnknown, result.Error!.Code);
    }

    [Fact]
    public void TheSubjectFallsBackToPropertiesType()
    {
        var result = EnvelopeReader.Read(Minimal(), propertiesType: "acme.orders.OrderCreated");

        Assert.Equal("acme.orders.OrderCreated", result.Envelope!.Subject!.Value);
    }

    [Fact]
    public void ADisagreementBetweenSubjectAndType_WarnsAndPrefersTheHeader()
    {
        var headers = Minimal();
        headers["concordat-subject"] = "acme.FromHeader";

        var result = EnvelopeReader.Read(headers, propertiesType: "acme.FromType");

        Assert.Equal("acme.FromHeader", result.Envelope!.Subject!.Value);
        Assert.Contains(
            result.Envelope.Warnings, w => w.Code == ConcordatCodes.EnvelopeSubjectTypeMismatch);
    }

    [Fact]
    public void ModeBIsReadWhenNoHeadersArePresent()
    {
        var result = EnvelopeReader.Read(
            headers: null,
            propertiesType: "acme.Order",
            contentType: $"application/json+concordat.v1.{ValidId}");

        Assert.Equal(EnvelopeKind.ContentType, result.Kind);
        Assert.Equal(ValidId, result.Envelope!.SchemaId.Value);
        Assert.Equal(SchemaFormat.Json, result.Envelope.Format);
    }

    [Fact]
    public void ModeATakesPrecedenceOverModeB()
    {
        var other = new string('b', 32);
        var result = EnvelopeReader.Read(
            Minimal(), contentType: $"application/json+concordat.v1.{other}");

        Assert.Equal(EnvelopeKind.Headers, result.Kind);
        Assert.Equal(ValidId, result.Envelope!.SchemaId.Value);
    }

    [Fact]
    public void AnOrdinaryContentTypeIsNotModeB() =>
        Assert.Equal(
            EnvelopeKind.None,
            EnvelopeReader.Read(headers: null, contentType: "application/json").Kind);

    [Fact]
    public void AModeBTokenWithAMalformedId_IsNotTreatedAsAnEnvelope() =>
        Assert.Equal(
            EnvelopeKind.None,
            EnvelopeReader.Read(headers: null, contentType: "application/json+concordat.v1.NOTHEX").Kind);
}
