using System.Globalization;
using Concordat.Domain.Registry;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Concordat.RabbitMq;

/// <summary>
/// An <see cref="IAsyncBasicConsumer"/> that verifies the envelope before your handler sees it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class never throws out of a delivery.</b> Not as a style preference — a throw from
/// <c>HandleBasicDeliverAsync</c> does not reach the application. RabbitMQ.Client routes it to
/// <c>CallbackExceptionAsync</c>, an event most codebases never subscribe to, and the delivery
/// is left unacknowledged. The visible symptom is a queue that stops draining for no stated
/// reason. So every failure here becomes a nack or a delivery, and every one of them is
/// reported.
/// </para>
/// <para>
/// The asymmetry with <see cref="ConcordatChannel"/> is deliberate and is the heart of M2.4. A
/// publisher can be refused: it is in-process, holding the message, able to fix it. A consumer
/// has none of that — by the time it sees a bad message, the mistake is already durable and
/// somebody else's.
/// </para>
/// </remarks>
public sealed class ConcordatConsumer : IAsyncBasicConsumer
{
    private readonly IAsyncBasicConsumer _inner;
    private readonly SchemaEnforcer _enforcer;
    private readonly ConcordatRabbitMqOptions _options;
    private readonly IChannel _channel;
    private readonly string? _queue;
    private int _quarantineDeclared;

    /// <summary>Wraps a consumer.</summary>
    /// <param name="inner">The application's consumer.</param>
    /// <param name="channel">
    /// The channel deliveries arrive on, used to nack and to publish to quarantine. Taken
    /// explicitly rather than from <paramref name="inner"/>, whose <c>Channel</c> may be null
    /// until it is registered.
    /// </param>
    /// <param name="enforcer">The shared enforcement rules.</param>
    /// <param name="options">Configuration.</param>
    /// <param name="queue">
    /// The queue being consumed, used to resolve the contract governing it (M7.3). Supplied
    /// automatically by <see cref="ConcordatChannel.BasicConsumeAsync"/>. Null leaves deliveries
    /// governed by <see cref="ConcordatRabbitMqOptions.Mode"/> alone, because RabbitMQ does not
    /// put the queue name on a delivery and there is nothing else to infer it from.
    /// </param>
    public ConcordatConsumer(
        IAsyncBasicConsumer inner,
        IChannel channel,
        SchemaEnforcer enforcer,
        ConcordatRabbitMqOptions options,
        string? queue = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(enforcer);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _inner = inner;
        _enforcer = enforcer;
        _options = options;
        _queue = queue;

        // Unwrapped deliberately. A quarantined message is by definition one that fails its
        // schema, so publishing it through an enforcing channel would refuse it — and the
        // refusal would be swallowed by TryQuarantineAsync's catch, leaving quarantine silently
        // broken in exactly the mode that needs it. Quarantine is plumbing, not traffic.
        _channel = channel is ConcordatChannel decorated ? decorated.Inner : channel;
    }

    /// <inheritdoc />
    public IChannel? Channel => _inner.Channel ?? _channel;

    /// <inheritdoc />
    public async Task HandleBasicDeliverAsync(
        string consumerTag,
        ulong deliveryTag,
        bool redelivered,
        string exchange,
        string routingKey,
        IReadOnlyBasicProperties properties,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        // Local Off is the one setting a contract cannot override, matching ConcordatChannel:
        // it means "Concordat does nothing in this process", and honouring that must not depend
        // on the registry being reachable. Everywhere above this line the contract wins.
        if (_options.Mode is EnforcementMode.Off)
        {
            await Deliver().ConfigureAwait(false);
            return;
        }

        EnforcementDecision decision;
        try
        {
            decision = await _enforcer.InspectConsumeAsync(
                AsReadOnly(properties.Headers),
                properties.Type,
                properties.ContentType,
                body,
                _queue,
                cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Deliberately broad: see below.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // A bug in Concordat must never eat somebody's message. Fail open, loudly: the
            // application gets its delivery and the observer gets told the middleware broke.
            // Letting this propagate would strand the delivery in CallbackExceptionAsync.
            Report(
                new EnforcementDecision(EnforcementOutcome.Unenforced, "middleware_faulted", ex.Message),
                EnforcementOutcome.Unenforced,
                exchange,
                routingKey);

            await Deliver().ConfigureAwait(false);
            return;
        }

        var violated = decision.Outcome is EnforcementOutcome.Observed;

        // EffectiveMode, not _options.Mode: the queue's contract has already decided.
        if (!violated || decision.EffectiveMode is EnforcementMode.Monitor)
        {
            Report(decision, decision.Outcome, exchange, routingKey);
            await Deliver().ConfigureAwait(false);
            return;
        }

        var quarantined = await TryQuarantineAsync(
            decision, exchange, routingKey, properties, body, cancellationToken).ConfigureAwait(false);

        if (!quarantined)
        {
            // Could not quarantine. The three options are: drop it, requeue it, or deliver it.
            // Dropping loses data silently; requeuing makes a poison loop out of a
            // deterministic failure. Delivering is the only one that is both visible and
            // recoverable, so the application gets an invalid message and the observer is told
            // exactly that.
            Report(decision, EnforcementOutcome.QuarantineFailed, exchange, routingKey);
            await Deliver().ConfigureAwait(false);
            return;
        }

        Report(decision, EnforcementOutcome.Quarantined, exchange, routingKey);

        // requeue: false. A schema violation is deterministic — the same message will fail the
        // same way forever, so redelivery is pure waste and a redelivery storm besides.
        await _channel.BasicNackAsync(deliveryTag, multiple: false, requeue: false, cancellationToken)
            .ConfigureAwait(false);

        return;

        Task Deliver() => _inner.HandleBasicDeliverAsync(
            consumerTag, deliveryTag, redelivered, exchange, routingKey, properties, body, cancellationToken);
    }

    private async Task<bool> TryQuarantineAsync(
        EnforcementDecision decision,
        string exchange,
        string routingKey,
        IReadOnlyBasicProperties properties,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_options.DeclareQuarantineExchange && Interlocked.Exchange(ref _quarantineDeclared, 1) == 0)
            {
                await _channel.ExchangeDeclareAsync(
                    _options.QuarantineExchange,
                    ExchangeType.Topic,
                    durable: true,
                    autoDelete: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var headers = properties.Headers is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(properties.Headers, StringComparer.Ordinal);

            // Why it was quarantined, and where it came from. Without the origin, a quarantine
            // exchange fed by several bindings is a pile of messages nobody can route back.
            // No x- prefix, for the same reason the envelope has none (ADR-013).
            headers["concordat-quarantine-reason"] = decision.Code ?? "payload_invalid";
            headers["concordat-quarantine-detail"] = Truncate(decision.Detail);
            headers["concordat-quarantine-exchange"] = exchange;
            headers["concordat-quarantine-routing-key"] = routingKey;
            headers["concordat-quarantine-at"] =
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            var quarantineProperties = new BasicProperties(properties) { Headers = headers };

            // The original routing key is kept, so an operator can bind selectively rather than
            // subscribing to every quarantined message in the estate.
            await _channel.BasicPublishAsync(
                _options.QuarantineExchange,
                routingKey,
                mandatory: false,
                quarantineProperties,
                body,
                cancellationToken).ConfigureAwait(false);

            return true;
        }
#pragma warning disable CA1031 // Any failure here must degrade, not propagate.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Re-declaring is cheap and idempotent; a failed attempt should not permanently
            // mark the exchange as declared.
            Interlocked.Exchange(ref _quarantineDeclared, 0);
            return false;
        }
    }

    private static string Truncate(string detail) =>
        // AMQP long strings are generous but Rebus truncates above 64 KiB (DESIGN §2), and a
        // validation report over every field of a large document can get there.
        detail.Length <= 4096 ? detail : detail[..4096] + "… (truncated)";

    private static IReadOnlyDictionary<string, object?>? AsReadOnly(IDictionary<string, object?>? headers) =>
        headers switch
        {
            null => null,
            IReadOnlyDictionary<string, object?> readOnly => readOnly,
            _ => new Dictionary<string, object?>(headers, StringComparer.Ordinal),
        };

    private void Report(
        EnforcementDecision decision, EnforcementOutcome outcome, string exchange, string routingKey) =>
        _options.Observer.Observe(new EnforcementEvent(
            EnforcementSide.Consume,
            outcome,
            decision.Code,
            decision.Detail,
            decision.Subject?.Value,
            decision.SchemaId?.Value,
            exchange,
            routingKey));

    /// <inheritdoc />
    public Task HandleBasicCancelAsync(string consumerTag, CancellationToken cancellationToken = default) =>
        _inner.HandleBasicCancelAsync(consumerTag, cancellationToken);

    /// <inheritdoc />
    public Task HandleBasicCancelOkAsync(string consumerTag, CancellationToken cancellationToken = default) =>
        _inner.HandleBasicCancelOkAsync(consumerTag, cancellationToken);

    /// <inheritdoc />
    public Task HandleBasicConsumeOkAsync(string consumerTag, CancellationToken cancellationToken = default) =>
        _inner.HandleBasicConsumeOkAsync(consumerTag, cancellationToken);

    /// <inheritdoc />
    public Task HandleChannelShutdownAsync(object channel, ShutdownEventArgs reason) =>
        _inner.HandleChannelShutdownAsync(channel, reason);
}
