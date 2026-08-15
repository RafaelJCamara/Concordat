using System.Security.Cryptography;
using Concordat.Application.Abstractions;
using Concordat.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Concordat.Infrastructure;

/// <summary>
/// Stores webhook signing secrets encrypted with ASP.NET Core Data Protection (M7.5).
/// </summary>
/// <remarks>
/// Same mechanism as <see cref="DataProtectionCredentialStore"/>, under its own purpose string
/// so a payload written for one can never be unprotected under the other.
/// </remarks>
public sealed class DataProtectionWebhookSigningKeyStore : IWebhookSigningKeyStore
{
    private const string Purpose = "Concordat.WebhookSigningKey.v1";
    private const int SecretBytes = 32;

    private readonly ConcordatDbContext _context;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;

    /// <summary>Creates the store.</summary>
    /// <param name="context">The database context.</param>
    /// <param name="provider">The Data Protection provider.</param>
    /// <param name="clock">The clock.</param>
    public DataProtectionWebhookSigningKeyStore(
        ConcordatDbContext context, IDataProtectionProvider provider, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(provider);

        _context = context;
        _protector = provider.CreateProtector(Purpose);
        _clock = clock;
    }

    /// <inheritdoc />
    public Task<(string Reference, string Secret)> GenerateAsync(CancellationToken cancellationToken)
    {
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(SecretBytes));
        var reference = Guid.CreateVersion7().ToString("N");

        _context.Set<StoredSigningKey>().Add(new StoredSigningKey
        {
            Reference = reference,
            Ciphertext = _protector.Protect(secret),
            UpdatedAt = _clock.GetUtcNow(),
        });

        return Task.FromResult((reference, secret));
    }

    /// <inheritdoc />
    public async Task<string?> ResolveAsync(string reference, CancellationToken cancellationToken)
    {
        var stored = await _context.Set<StoredSigningKey>()
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Reference == reference, cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(stored.Ciphertext);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // An undecryptable payload means the key ring lost the key that wrote it. The
            // honest answer is "no usable secret", which surfaces as an unsigned-delivery
            // decision an operator can act on rather than an unhandled exception mid-delivery.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string reference, CancellationToken cancellationToken)
    {
        var stored = await _context.Set<StoredSigningKey>()
            .FirstOrDefaultAsync(k => k.Reference == reference, cancellationToken)
            .ConfigureAwait(false);

        if (stored is not null)
        {
            _context.Set<StoredSigningKey>().Remove(stored);
        }
    }
}
