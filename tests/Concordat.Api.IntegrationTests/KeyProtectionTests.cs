using Concordat.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// M9.1's key-ring wrapping, and the one property that has to hold: a deployment never
/// believes its keys are protected when they are not.
/// </summary>
/// <remarks>
/// These build a bare <see cref="WebApplicationBuilder"/> rather than a host — the decision
/// under test happens at configuration time, and standing up a database to observe it would
/// test the database.
/// </remarks>
public class KeyProtectionTests
{
    private static WebApplicationBuilder BuilderWith(string? keyUri)
    {
        var builder = WebApplication.CreateBuilder();

        builder.Configuration.AddInMemoryCollection(
            [new KeyValuePair<string, string?>("Concordat:KeyProtection:KeyUri", keyUri)]);

        return builder;
    }

    [Fact]
    public void SelfHostedWithoutAKeyUriIsFine()
    {
        // An operator running one container against their own database has no KMS, and ADR-008
        // promises they do not need to stand one up. The key ring is still in the database
        // rather than on disk, which is what M7.2 was for.
        var builder = BuilderWith(null);

        var exception = Record.Exception(
            () => builder.AddConcordatKeyProtection(ConcordatProfile.SelfHosted));

        Assert.Null(exception);
    }

    [Fact]
    public void CloudWithoutAKeyUriRefusesToStart()
    {
        // The property this setting exists for. Falling through to an unwrapped key ring would
        // look identical from every screen, and the deployment would only discover it during
        // the incident where it matters: a database dump is then enough to read every tenant's
        // broker passwords.
        var builder = BuilderWith(null);

        var exception = Assert.Throws<InvalidOperationException>(
            () => builder.AddConcordatKeyProtection(ConcordatProfile.Cloud));

        Assert.Contains("KeyUri", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Cloud", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedKeyUriIsRefusedRatherThanIgnored()
    {
        // Ignoring it is the same failure as omitting it, arrived at by a typo.
        var builder = BuilderWith("not-a-uri");

        var exception = Assert.Throws<InvalidOperationException>(
            () => builder.AddConcordatKeyProtection(ConcordatProfile.Cloud));

        Assert.Contains("absolute URI", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AConfiguredKeyUriIsAcceptedWithoutReachingTheVault()
    {
        // Registration must not perform I/O: a vault round trip at startup would make the API
        // unable to boot during a Key Vault blip, and the credential is resolved lazily on
        // first use anyway.
        var builder = BuilderWith("https://example.vault.azure.net/keys/concordat/abc");

        var exception = Record.Exception(
            () => builder.AddConcordatKeyProtection(ConcordatProfile.Cloud));

        Assert.Null(exception);
    }
}
