using System.Text.Json;
using Concordat.Application.Abstractions;
using Concordat.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Concordat.Infrastructure;

/// <summary>
/// Stores broker credentials encrypted with ASP.NET Core Data Protection (M7.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>The purpose is defined, not just the algorithm.</b> A protector is scoped to a purpose
/// string, and a payload produced for one purpose cannot be unprotected under another. That
/// turns "an attacker who can write to some other protected field" into a failed decryption
/// rather than a credential swap.
/// </para>
/// <para>
/// <b>The key ring lives in the database</b> — see <c>ConcordatDbContext</c>. A disk ring is
/// the default and would be the wrong default here: a containerised registry restarting
/// without a mounted volume would generate a fresh key and every stored credential would
/// become permanently unreadable, with nothing to say why.
/// </para>
/// </remarks>
public sealed class DataProtectionCredentialStore : ICredentialStore
{
    /// <summary>
    /// The protector purpose. Changing this string makes every existing payload undecryptable,
    /// so it is a constant rather than anything derived.
    /// </summary>
    private const string Purpose = "Concordat.BrokerCredential.v1";

    private readonly ConcordatDbContext _context;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;

    /// <summary>Creates the store.</summary>
    /// <param name="context">The database context.</param>
    /// <param name="provider">The Data Protection provider.</param>
    /// <param name="clock">The clock.</param>
    public DataProtectionCredentialStore(
        ConcordatDbContext context, IDataProtectionProvider provider, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(provider);

        _context = context;
        _protector = provider.CreateProtector(Purpose);
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<string> StoreAsync(
        BrokerCredential credential, string? existingRef, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var ciphertext = _protector.Protect(JsonSerializer.Serialize(credential));

        // Replacing in place when the broker already had a reference, so rotating a password
        // cannot leave the previous secret sitting in the table under an orphaned key.
        if (existingRef is { Length: > 0 })
        {
            var existing = await _context.Set<StoredCredential>()
                .FirstOrDefaultAsync(c => c.Reference == existingRef, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                existing.Ciphertext = ciphertext;
                existing.UpdatedAt = _clock.GetUtcNow();
                return existing.Reference;
            }
        }

        var reference = Guid.CreateVersion7().ToString("N");

        _context.Set<StoredCredential>().Add(new StoredCredential
        {
            Reference = reference,
            Ciphertext = ciphertext,
            UpdatedAt = _clock.GetUtcNow(),
        });

        return reference;
    }

    /// <inheritdoc />
    public async Task<BrokerCredential?> ResolveAsync(
        string credentialRef, CancellationToken cancellationToken)
    {
        var stored = await _context.Set<StoredCredential>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Reference == credentialRef, cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BrokerCredential>(
                _protector.Unprotect(stored.Ciphertext));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // An undecryptable payload means the key ring lost the key that wrote it. The
            // honest answer is "no usable credential", which surfaces as a failed connection
            // an operator can act on — rather than an unhandled exception on a read path.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string credentialRef, CancellationToken cancellationToken)
    {
        var stored = await _context.Set<StoredCredential>()
            .FirstOrDefaultAsync(c => c.Reference == credentialRef, cancellationToken)
            .ConfigureAwait(false);

        if (stored is not null)
        {
            _context.Set<StoredCredential>().Remove(stored);
        }
    }
}
