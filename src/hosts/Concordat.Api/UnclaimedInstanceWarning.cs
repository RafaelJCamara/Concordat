using Concordat.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Concordat.Api;

/// <summary>
/// Says out loud that this instance is unclaimed, and keeps saying it (decision 27).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AuthenticationOptions.AllowAnonymousUntilClaimed"/> means an unauthenticated
/// request is answered as an owner until the first account exists, so <c>docker compose up</c>
/// gives you something usable and an upgrade does not lock anyone out. The residual risk was
/// never the mechanism — it is an instance nobody ever claims, wide open to whoever can reach
/// it, with <b>nothing anywhere saying so</b>.
/// </para>
/// <para>
/// <b>Repeated, not logged once at startup.</b> A single line at boot is exactly as invisible as
/// no line at all by the time it matters: the container has been running for three weeks and
/// that log scrolled away on day one. Repeating turns a transient message into a standing
/// signal, and it stops the moment somebody claims the instance — so the noise ends by being
/// acted on, which is the only kind of alert worth having.
/// </para>
/// <para>
/// <b>It stops itself.</b> Once claimed, the loop exits rather than continuing to poll: the
/// question can never become true again, because claiming is one-way.
/// </para>
/// </remarks>
public sealed partial class UnclaimedInstanceWarning(
    IServiceScopeFactory scopes,
    IOptions<AuthenticationOptions> options,
    ILogger<UnclaimedInstanceWarning> logger) : BackgroundService
{
    /// <summary>How long between repeats of the warning.</summary>
    /// <remarks>
    /// Long enough not to be noise in a log somebody greps, short enough that an instance left
    /// open over a weekend has said so more than once.
    /// </remarks>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>How long to wait before the first check.</summary>
    /// <remarks>
    /// Not zero. The first request to a cold instance runs migrations-adjacent work and warms
    /// pools; adding a database round trip to the same instant buys nothing and makes a slow
    /// start slower.
    /// </remarks>
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.AllowAnonymousUntilClaimed)
        {
            // Turned off, so an unclaimed instance answers nobody and there is nothing to warn
            // about. Returning rather than looping silently.
            return;
        }

        var delay = InitialDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);

                await using var scope = scopes.CreateAsyncScope();

                var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

                if (await users.AnyAsync(stoppingToken).ConfigureAwait(false))
                {
                    Log.Claimed(logger);
                    return;
                }

                Log.Unclaimed(logger);
                delay = Interval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Deliberately broad: see below.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // A warning that cannot reach the database must not take the host down with it,
                // and must not stop trying either — the condition it reports is a security one,
                // and "the check crashed" is the worst possible reason for it to go quiet.
                Log.CheckFailed(logger, ex);
                delay = Interval;
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 8001,
            Level = LogLevel.Warning,
            Message = "THIS INSTANCE IS UNCLAIMED. It has no accounts, so every unauthenticated " +
                      "request is answered as an owner and anyone who can reach it can read and " +
                      "change everything. Claim it with POST /v1/auth/bootstrap, or set " +
                      "Concordat:Authentication:AllowAnonymousUntilClaimed=false to close it now.")]
        public static partial void Unclaimed(ILogger logger);

        [LoggerMessage(
            EventId = 8002,
            Level = LogLevel.Information,
            Message = "This instance has been claimed; anonymous access is no longer granted.")]
        public static partial void Claimed(ILogger logger);

        [LoggerMessage(
            EventId = 8003,
            Level = LogLevel.Warning,
            Message = "Could not check whether this instance is claimed; will retry.")]
        public static partial void CheckFailed(ILogger logger, Exception exception);
    }
}
