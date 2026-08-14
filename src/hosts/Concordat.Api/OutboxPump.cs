using Concordat.Application.Governance;

namespace Concordat.Api;

/// <summary>
/// Drains the notification outbox on a timer (M7.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>In-process, and that is a deliberate limit.</b> Every instance polls, so two instances
/// can claim the same message and deliver it twice — which is acceptable precisely because the
/// contract with receivers is already at-least-once and every notification carries an id to
/// deduplicate on. A single-writer election would be the alternative, and it would add a
/// leadership protocol to buy a guarantee this design has already declined to make.
/// </para>
/// <para>
/// <b>It never lets an exception escape.</b> A background service that throws is stopped by the
/// host and never runs again, so the registry would keep accepting changes and quietly stop
/// telling anyone about them — the exact failure notifications exist to prevent.
/// </para>
/// </remarks>
public sealed partial class OutboxPump(
    IServiceScopeFactory scopes, ILogger<OutboxPump> logger) : BackgroundService
{
    /// <summary>How long to wait between passes when there was nothing to do.</summary>
    public static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(15);

    /// <summary>How long to wait after a pass that delivered a full batch.</summary>
    /// <remarks>Short, so a backlog is worked through rather than trickled out at one batch per idle period.</remarks>
    public static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = IdleInterval;

            try
            {
                await using var scope = scopes.CreateAsyncScope();

                var dispatcher = scope.ServiceProvider
                    .GetRequiredService<NotificationDispatcher>();

                var result = await dispatcher.PumpAsync(stoppingToken).ConfigureAwait(false);

                if (result.Claimed > 0)
                {
                    Log.Pass(logger, result.Claimed, result.Delivered, result.Failed, result.Parked);
                }

                if (result.Claimed >= NotificationDispatcher.BatchSize)
                {
                    delay = BusyInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Broad, and load-bearing. See the remarks on the type: a pump that dies takes
                // every future notification with it, silently.
                Log.PassFailed(logger, ex);
            }

            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Source-generated log messages, so nothing is formatted when logging is off.</summary>
    private static partial class Log
    {
        [LoggerMessage(
            EventId = 7501,
            Level = LogLevel.Information,
            Message = "Outbox pass: {Claimed} claimed, {Delivered} delivered, {Failed} failed, {Parked} parked")]
        public static partial void Pass(
            ILogger logger, int claimed, int delivered, int failed, int parked);

        [LoggerMessage(
            EventId = 7502,
            Level = LogLevel.Error,
            Message = "An outbox pass failed. The next pass will retry.")]
        public static partial void PassFailed(ILogger logger, Exception exception);
    }
}
