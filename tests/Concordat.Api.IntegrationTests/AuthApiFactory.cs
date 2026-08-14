using System.Net;
using System.Net.Http.Json;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// An API host with its own database, claimed by an owner (M8).
/// </summary>
/// <remarks>
/// <b>Separate from <see cref="ApiFactory"/> on purpose.</b> Claiming an instance is a one-way
/// change: the moment an account exists, the unclaimed-instance caller stops applying and every
/// unauthenticated request in every other suite starts failing. Sharing one database would make
/// the whole integration suite depend on test ordering.
/// </remarks>
public sealed class AuthApiFactory : ApiFactory
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _ownerCredential;

    /// <summary>The owner's login.</summary>
    public const string OwnerEmail = "owner@example.com";

    /// <summary>The owner's password. Long enough to satisfy the rule, and nothing more.</summary>
    public const string OwnerPassword = "correct horse battery";

    /// <summary>Claims the instance if nobody has, and returns a credential for the owner.</summary>
    /// <returns>A bearer credential.</returns>
    public async Task<string> OwnerCredentialAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_ownerCredential is not null)
            {
                return _ownerCredential;
            }

            using var client = CreateClient();

            var bootstrap = await client.PostAsJsonAsync(
                "/v1/auth/bootstrap",
                new BootstrapRequest(OwnerEmail, OwnerPassword, "Owner"),
                Json).ConfigureAwait(false);

            Assert.Equal(HttpStatusCode.Created, bootstrap.StatusCode);

            var signIn = await client.PostAsJsonAsync(
                "/v1/auth/signin",
                new SignInRequest(OwnerEmail, OwnerPassword),
                Json).ConfigureAwait(false);

            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

            _ownerCredential =
                (await ReadAsync<SignInResponse>(signIn).ConfigureAwait(false)).Credential;

            return _ownerCredential;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Marks a class as sharing the claimed API host and its database.</summary>
[CollectionDefinition(Name)]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "'Collection' is xunit's own term for a shared-fixture group.")]
public sealed class AuthApiCollection : ICollectionFixture<AuthApiFactory>
{
    /// <summary>The collection name.</summary>
    public const string Name = "auth-api";
}
