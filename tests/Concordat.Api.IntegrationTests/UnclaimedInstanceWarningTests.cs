using Concordat.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Concordat.Api.IntegrationTests;

/// <summary>
/// The warning that an instance is unclaimed (decision 27).
/// </summary>
/// <remarks>
/// No database and no host: what is worth pinning here is that the option is <b>read</b>. This
/// codebase has shipped a declared-and-never-consulted setting twice — `fail-open | fail-closed`
/// in M2.1 and `RegistrationPolicy` in M7.1 — and both looked exactly like working configuration
/// from the outside.
/// </remarks>
public class UnclaimedInstanceWarningTests
{
    /// <summary>A scope factory that fails the test if anything asks it for a scope.</summary>
    private sealed class ForbiddenScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException(
                "The warning polled the database despite anonymous access being disabled.");
    }

    [Fact]
    public async Task ItDoesNotPollWhenAnonymousAccessIsAlreadyDisabled()
    {
        // Nothing to warn about: an unclaimed instance with the option off answers nobody, so a
        // timer waking every hour to say so would be a database round trip bought with nothing.
        var warning = new UnclaimedInstanceWarning(
            new ForbiddenScopes(),
            Options.Create(new AuthenticationOptions { AllowAnonymousUntilClaimed = false }),
            NullLogger<UnclaimedInstanceWarning>.Instance);

        await warning.StartAsync(CancellationToken.None);
        await warning.StopAsync(CancellationToken.None);
    }

    // There is deliberately no "the API registers it" test here. ApiFactory strips this
    // service along with the outbox pump, so any such assertion would be testing the fixture
    // rather than the host. Registration was verified by running the real container and reading
    // the log, which is recorded in docs/STATUS.md.
}
