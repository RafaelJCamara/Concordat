using Concordat.Domain.Registry;

namespace Concordat.Domain.Tests;

/// <summary>
/// Guards the envelope header names as protocol rather than as implementation detail.
/// </summary>
/// <remarks>
/// Before these existed as constants the names lived only in prose, so a rename in DESIGN §2
/// or a typo in one of five SDKs was invisible to every build and would only have surfaced
/// cross-language.
/// </remarks>
public class EnvelopeHeaderTests
{
    [Fact]
    public void TheHeaderSetIsExactlyTheSixDefinedByAdr010()
    {
        // Pinned deliberately. Adding a seventh header is an envelope change, and any envelope
        // change is expensive once Tier 2 SDKs exist - it should break a test and force the
        // conversation, not slip in.
        Assert.Equal(
            [
                "concordat-v",
                "concordat-schema-id",
                "concordat-subject",
                "concordat-version",
                "concordat-semver",
                "concordat-format",
            ],
            EnvelopeHeaders.All);
    }

    [Fact]
    public void NoHeaderUsesTheXPrefix()
    {
        // RabbitMQ turns x- headers into AMQP 1.0 message-annotations rather than
        // application-properties. Avoiding the prefix is the entire basis of ADR-013's
        // "designed to survive 1.0 conversion" claim.
        Assert.DoesNotContain(
            EnvelopeHeaders.All,
            h => h.StartsWith("x-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoHeaderCollidesWithAnotherLibrarysNamespace()
    {
        // A collision here would not fail validation - it would corrupt another library's
        // routing, silently.
        foreach (var header in EnvelopeHeaders.All)
        {
            foreach (var prefix in EnvelopeHeaders.ForeignPrefixes)
            {
                Assert.False(
                    header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase),
                    $"'{header}' collides with the '{prefix}' namespace.");
            }
        }
    }

    [Fact]
    public void EveryHeaderIsLowercaseAscii()
    {
        // Header lookup is ordinal and case-sensitive. Mixed case would invite an
        // implementer in another language to reach for case-folding canonicalisation and
        // silently diverge.
        foreach (var header in EnvelopeHeaders.All)
        {
            Assert.Equal(header.ToLowerInvariant(), header);
            Assert.All(header, c => Assert.InRange(c, (char)0x20, (char)0x7E));
        }
    }

    [Fact]
    public void TheCurrentVersionIsTheBareNumber() =>
        // Not "v1", not "1.0". Pinned because it is the value every SDK compares against.
        Assert.Equal("1", EnvelopeHeaders.CurrentVersion);

    [Fact]
    public void EveryHeaderNameIsDistinct() =>
        Assert.Equal(EnvelopeHeaders.All.Count, EnvelopeHeaders.All.Distinct(StringComparer.Ordinal).Count());
}
