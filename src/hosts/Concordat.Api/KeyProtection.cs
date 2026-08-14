using Azure.Identity;
using Concordat.Infrastructure;
using Concordat.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;

namespace Concordat.Api;

/// <summary>Where the Data Protection key ring's own encryption key lives (M9.1).</summary>
public sealed class KeyProtectionOptions
{
    /// <summary>
    /// An Azure Key Vault key URI, or empty to leave the key ring unwrapped.
    /// </summary>
    /// <remarks>
    /// A <em>key</em> URI, not a secret or a certificate:
    /// <c>https://vault.vault.azure.net/keys/concordat/version</c>. Key Vault wraps the key
    /// ring's keys with it and never releases the key material, which is the property that
    /// makes a database dump insufficient to read a stored broker credential.
    /// </remarks>
    public string? KeyUri { get; set; }
}

/// <summary>Wires the Data Protection key ring (M7.2, M9.1).</summary>
public static class KeyProtection
{
    /// <summary>Registers key persistence and, where configured, key wrapping.</summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="profile">Which deployment flavour this process is.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// <b>Persistence and protection are separate decisions.</b> The key ring lives in
    /// PostgreSQL in both flavours (M7.2) so a second API instance can decrypt what the first
    /// one wrote — a disk ring would silently destroy every stored credential the first time a
    /// container restarted without a mounted volume. What M9.1 adds is <em>wrapping</em> those
    /// keys, so the keys that decrypt every broker credential are not sitting unprotected in
    /// the same database as the ciphertext they protect.
    /// </para>
    /// <para>
    /// <b>Cloud without a key URI refuses to start.</b> That is the whole point of the setting:
    /// a deployment that believes its keys are wrapped and finds out otherwise during an
    /// incident is worse off than one that never claimed it. Self-hosted is left free — an
    /// operator running one container against their own database has no KMS and should not be
    /// made to stand one up (ADR-008).
    /// </para>
    /// </remarks>
    public static WebApplicationBuilder AddConcordatKeyProtection(
        this WebApplicationBuilder builder, ConcordatProfile profile)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Configuration
            .GetSection("Concordat:KeyProtection").Get<KeyProtectionOptions>()
            ?? new KeyProtectionOptions();

        var keys = builder.Services.AddDataProtection()
            .SetApplicationName("Concordat")
            .PersistKeysToDbContext<ConcordatDbContext>();

        if (string.IsNullOrWhiteSpace(options.KeyUri))
        {
            if (profile is ConcordatProfile.Cloud)
            {
                throw new InvalidOperationException(
                    "Concordat:KeyProtection:KeyUri is required in the Cloud profile. Without " +
                    "it the key ring is stored unwrapped alongside the credentials it " +
                    "protects, so a database dump is enough to read every tenant's broker " +
                    "passwords. Set it to an Azure Key Vault key URI, or run the SelfHosted " +
                    "profile.");
            }

            return builder;
        }

        if (!Uri.TryCreate(options.KeyUri, UriKind.Absolute, out var keyUri))
        {
            throw new InvalidOperationException(
                $"Concordat:KeyProtection:KeyUri is not an absolute URI: '{options.KeyUri}'.");
        }

        // DefaultAzureCredential, so the same image authenticates by managed identity in AKS,
        // by a service principal in CI, and by the developer's own az login locally — without
        // a credential ever being configured into the deployment. Failing here is deliberate:
        // an unreachable vault must stop the process rather than quietly fall through to
        // unwrapped keys, which would look identical from every screen.
        keys.ProtectKeysWithAzureKeyVault(keyUri, new DefaultAzureCredential());

        return builder;
    }
}
