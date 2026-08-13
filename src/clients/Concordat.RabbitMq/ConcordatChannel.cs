using Concordat.Domain.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Concordat.RabbitMq;

/// <summary>
/// An <see cref="IChannel"/> that stamps and enforces the envelope on the way out.
/// </summary>
/// <remarks>
/// <para>
/// <b>A full decorator rather than an extension method, and that is the design.</b> An opt-in
/// <c>PublishWithConcordatAsync</c> would leave every ordinary <c>BasicPublishAsync</c> in the
/// codebase publishing unchecked — including the ones added next quarter by someone who has
/// never heard of Concordat. Enforcement you can bypass by forgetting is the failure mode this
/// product exists to prevent, so the only publish path is the enforced one.
/// </para>
/// <para>
/// Everything except publishing forwards untouched. The channel is not a place to be clever.
/// </para>
/// </remarks>
public sealed class ConcordatChannel : IChannel
{
    private readonly IChannel _inner;
    private readonly SchemaEnforcer _enforcer;
    private readonly ConcordatRabbitMqOptions _options;

    /// <summary>Wraps a channel.</summary>
    /// <param name="inner">The real channel.</param>
    /// <param name="enforcer">The shared enforcement rules.</param>
    /// <param name="options">Configuration.</param>
    public ConcordatChannel(IChannel inner, SchemaEnforcer enforcer, ConcordatRabbitMqOptions options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(enforcer);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _inner = inner;
        _enforcer = enforcer;
        _options = options;
    }

    /// <summary>The channel underneath, for topology work that does not need enforcing.</summary>
    public IChannel Inner => _inner;

    /// <inheritdoc />
    public ValueTask BasicPublishAsync<TProperties>(
        string exchange,
        string routingKey,
        bool mandatory,
        TProperties basicProperties,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
        where TProperties : IReadOnlyBasicProperties, IAmqpHeader =>
        PublishAsync(exchange, routingKey, mandatory, basicProperties, body, cancellationToken);

    /// <inheritdoc />
    public ValueTask BasicPublishAsync<TProperties>(
        CachedString exchange,
        CachedString routingKey,
        bool mandatory,
        TProperties basicProperties,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
        where TProperties : IReadOnlyBasicProperties, IAmqpHeader =>
        PublishAsync(exchange.Value, routingKey.Value, mandatory, basicProperties, body, cancellationToken);

    private async ValueTask PublishAsync<TProperties>(
        string exchange,
        string routingKey,
        bool mandatory,
        TProperties basicProperties,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
        where TProperties : IReadOnlyBasicProperties, IAmqpHeader
    {
        if (_options.Mode is EnforcementMode.Off)
        {
            // Off must cost nothing and touch nothing. No resolution, no validation, no copy.
            await _inner
                .BasicPublishAsync(exchange, routingKey, mandatory, basicProperties, body, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var decision = await _enforcer.InspectPublishAsync(
            new PublishContext
            {
                MessageType = basicProperties.Type,
                Exchange = exchange,
                RoutingKey = routingKey,
                Headers = AsReadOnly(basicProperties.Headers),
            },
            body,
            cancellationToken).ConfigureAwait(false);

        var violated = decision.Outcome is EnforcementOutcome.Observed;

        if (violated && _options.Mode is EnforcementMode.Enforce)
        {
            Report(decision, EnforcementOutcome.Blocked, exchange, routingKey);

            // A throw blocks the publish, which is the whole point on this side. The publisher
            // is in-process, holding the message, able to fix it and retry — unlike a consumer,
            // which has none of those things.
            throw new ConcordatViolationException(
                decision.Code ?? "payload_invalid",
                $"Publish to '{exchange}' with routing key '{routingKey}' was refused: {decision.Detail}",
                decision.Subject?.Value,
                decision.SchemaId?.Value);
        }

        Report(decision, decision.Outcome, exchange, routingKey);

        // The envelope is stamped even when the payload violated its schema, provided identity
        // was resolvable. In Monitor mode that is what lets consumers begin reading schema ids
        // before every publisher is clean.
        if (decision.Envelope is null)
        {
            await _inner
                .BasicPublishAsync(exchange, routingKey, mandatory, basicProperties, body, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var stamped = Stamp(basicProperties, decision.Envelope);

        await _inner
            .BasicPublishAsync(exchange, routingKey, mandatory, stamped, body, cancellationToken)
            .ConfigureAwait(false);
    }

    private static BasicProperties Stamp<TProperties>(
        TProperties source, IReadOnlyDictionary<string, string> envelope)
        where TProperties : IReadOnlyBasicProperties
    {
        // Copied rather than mutated: TProperties is constrained to IReadOnlyBasicProperties,
        // and a caller reusing one properties object across publishes must not find our
        // headers accumulating on it.
        var properties = new BasicProperties(source)
        {
            Headers = source.Headers is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(source.Headers, StringComparer.Ordinal),
        };

        foreach (var (key, value) in envelope)
        {
            properties.Headers[key] = value;
        }

        return properties;
    }

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
            EnforcementSide.Publish,
            outcome,
            decision.Code,
            decision.Detail,
            decision.Subject?.Value,
            decision.SchemaId?.Value,
            exchange,
            routingKey));

    // ---- Everything below forwards. ----

    /// <inheritdoc />
    public int ChannelNumber => _inner.ChannelNumber;

    /// <inheritdoc />
    public ShutdownEventArgs? CloseReason => _inner.CloseReason;

    /// <inheritdoc />
    public IAsyncBasicConsumer? DefaultConsumer
    {
        get => _inner.DefaultConsumer;
        set => _inner.DefaultConsumer = value;
    }

    /// <inheritdoc />
    public bool IsClosed => _inner.IsClosed;

    /// <inheritdoc />
    public bool IsOpen => _inner.IsOpen;

    /// <inheritdoc />
    public string? CurrentQueue => _inner.CurrentQueue;

    /// <inheritdoc />
    public TimeSpan ContinuationTimeout
    {
        get => _inner.ContinuationTimeout;
        set => _inner.ContinuationTimeout = value;
    }

    /// <inheritdoc />
    public event AsyncEventHandler<BasicAckEventArgs> BasicAcksAsync
    {
        add => _inner.BasicAcksAsync += value;
        remove => _inner.BasicAcksAsync -= value;
    }

    /// <inheritdoc />
    public event AsyncEventHandler<BasicNackEventArgs> BasicNacksAsync
    {
        add => _inner.BasicNacksAsync += value;
        remove => _inner.BasicNacksAsync -= value;
    }

    /// <inheritdoc />
    public event AsyncEventHandler<BasicReturnEventArgs> BasicReturnAsync
    {
        add => _inner.BasicReturnAsync += value;
        remove => _inner.BasicReturnAsync -= value;
    }

    /// <inheritdoc />
    public event AsyncEventHandler<CallbackExceptionEventArgs> CallbackExceptionAsync
    {
        add => _inner.CallbackExceptionAsync += value;
        remove => _inner.CallbackExceptionAsync -= value;
    }

    /// <inheritdoc />
    public event AsyncEventHandler<FlowControlEventArgs> FlowControlAsync
    {
        add => _inner.FlowControlAsync += value;
        remove => _inner.FlowControlAsync -= value;
    }

    /// <inheritdoc />
    public event AsyncEventHandler<ShutdownEventArgs> ChannelShutdownAsync
    {
        add => _inner.ChannelShutdownAsync += value;
        remove => _inner.ChannelShutdownAsync -= value;
    }

    /// <inheritdoc />
    public ValueTask BasicAckAsync(ulong deliveryTag, bool multiple, CancellationToken cancellationToken = default) =>
        _inner.BasicAckAsync(deliveryTag, multiple, cancellationToken);

    /// <inheritdoc />
    public Task BasicCancelAsync(string consumerTag, bool noWait = false, CancellationToken cancellationToken = default) =>
        _inner.BasicCancelAsync(consumerTag, noWait, cancellationToken);

    /// <inheritdoc />
    public Task<string> BasicConsumeAsync(
        string queue,
        bool autoAck,
        string consumerTag,
        bool noLocal,
        bool exclusive,
        IDictionary<string, object?>? arguments,
        IAsyncBasicConsumer consumer,
        CancellationToken cancellationToken = default) =>
        _inner.BasicConsumeAsync(queue, autoAck, consumerTag, noLocal, exclusive, arguments, consumer, cancellationToken);

    /// <inheritdoc />
    public Task<BasicGetResult?> BasicGetAsync(string queue, bool autoAck, CancellationToken cancellationToken = default) =>
        _inner.BasicGetAsync(queue, autoAck, cancellationToken);

    /// <inheritdoc />
    public ValueTask BasicNackAsync(ulong deliveryTag, bool multiple, bool requeue, CancellationToken cancellationToken = default) =>
        _inner.BasicNackAsync(deliveryTag, multiple, requeue, cancellationToken);

    /// <inheritdoc />
    public Task BasicQosAsync(uint prefetchSize, ushort prefetchCount, bool global, CancellationToken cancellationToken = default) =>
        _inner.BasicQosAsync(prefetchSize, prefetchCount, global, cancellationToken);

    /// <inheritdoc />
    public ValueTask BasicRejectAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken = default) =>
        _inner.BasicRejectAsync(deliveryTag, requeue, cancellationToken);

    /// <inheritdoc />
    public Task CloseAsync(ushort replyCode, string replyText, bool abort, CancellationToken cancellationToken = default) =>
        _inner.CloseAsync(replyCode, replyText, abort, cancellationToken);

    /// <inheritdoc />
    [Obsolete("7.2.0 - cancellationToken is ignored")]
    public Task CloseAsync(ShutdownEventArgs reason, bool abort, CancellationToken cancellationToken) =>
        _inner.CloseAsync(reason, abort, cancellationToken);

    /// <inheritdoc />
    public Task CloseAsync(ShutdownEventArgs reason, bool abort) => _inner.CloseAsync(reason, abort);

    /// <inheritdoc />
    public Task<uint> ConsumerCountAsync(string queue, CancellationToken cancellationToken = default) =>
        _inner.ConsumerCountAsync(queue, cancellationToken);

    /// <inheritdoc />
    public Task ExchangeBindAsync(
        string destination,
        string source,
        string routingKey,
        IDictionary<string, object?>? arguments = null,
        bool noWait = false,
        CancellationToken cancellationToken = default) =>
        _inner.ExchangeBindAsync(destination, source, routingKey, arguments, noWait, cancellationToken);

    /// <inheritdoc />
    public Task ExchangeDeclareAsync(
        string exchange,
        string type,
        bool durable,
        bool autoDelete,
        IDictionary<string, object?>? arguments = null,
        bool passive = false,
        bool noWait = false,
        CancellationToken cancellationToken = default) =>
        _inner.ExchangeDeclareAsync(exchange, type, durable, autoDelete, arguments, passive, noWait, cancellationToken);

    /// <inheritdoc />
    public Task ExchangeDeclarePassiveAsync(string exchange, CancellationToken cancellationToken = default) =>
        _inner.ExchangeDeclarePassiveAsync(exchange, cancellationToken);

    /// <inheritdoc />
    public Task ExchangeDeleteAsync(string exchange, bool ifUnused = false, bool noWait = false, CancellationToken cancellationToken = default) =>
        _inner.ExchangeDeleteAsync(exchange, ifUnused, noWait, cancellationToken);

    /// <inheritdoc />
    public Task ExchangeUnbindAsync(
        string destination,
        string source,
        string routingKey,
        IDictionary<string, object?>? arguments = null,
        bool noWait = false,
        CancellationToken cancellationToken = default) =>
        _inner.ExchangeUnbindAsync(destination, source, routingKey, arguments, noWait, cancellationToken);

    /// <inheritdoc />
    public ValueTask<ulong> GetNextPublishSequenceNumberAsync(CancellationToken cancellationToken = default) =>
        _inner.GetNextPublishSequenceNumberAsync(cancellationToken);

    /// <inheritdoc />
    public Task<uint> MessageCountAsync(string queue, CancellationToken cancellationToken = default) =>
        _inner.MessageCountAsync(queue, cancellationToken);

    /// <inheritdoc />
    public Task QueueBindAsync(
        string queue,
        string exchange,
        string routingKey,
        IDictionary<string, object?>? arguments = null,
        bool noWait = false,
        CancellationToken cancellationToken = default) =>
        _inner.QueueBindAsync(queue, exchange, routingKey, arguments, noWait, cancellationToken);

    /// <inheritdoc />
    public Task<QueueDeclareOk> QueueDeclareAsync(
        string queue,
        bool durable,
        bool exclusive,
        bool autoDelete,
        IDictionary<string, object?>? arguments = null,
        bool passive = false,
        bool noWait = false,
        CancellationToken cancellationToken = default) =>
        _inner.QueueDeclareAsync(queue, durable, exclusive, autoDelete, arguments, passive, noWait, cancellationToken);

    /// <inheritdoc />
    public Task<QueueDeclareOk> QueueDeclarePassiveAsync(string queue, CancellationToken cancellationToken = default) =>
        _inner.QueueDeclarePassiveAsync(queue, cancellationToken);

    /// <inheritdoc />
    public Task<uint> QueueDeleteAsync(string queue, bool ifUnused, bool ifEmpty, bool noWait = false, CancellationToken cancellationToken = default) =>
        _inner.QueueDeleteAsync(queue, ifUnused, ifEmpty, noWait, cancellationToken);

    /// <inheritdoc />
    public Task<uint> QueuePurgeAsync(string queue, CancellationToken cancellationToken = default) =>
        _inner.QueuePurgeAsync(queue, cancellationToken);

    /// <inheritdoc />
    public Task QueueUnbindAsync(
        string queue,
        string exchange,
        string routingKey,
        IDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default) =>
        _inner.QueueUnbindAsync(queue, exchange, routingKey, arguments, cancellationToken);

    /// <inheritdoc />
    public Task TxCommitAsync(CancellationToken cancellationToken = default) => _inner.TxCommitAsync(cancellationToken);

    /// <inheritdoc />
    public Task TxRollbackAsync(CancellationToken cancellationToken = default) => _inner.TxRollbackAsync(cancellationToken);

    /// <inheritdoc />
    public Task TxSelectAsync(CancellationToken cancellationToken = default) => _inner.TxSelectAsync(cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>A message was refused because it did not conform to its schema.</summary>
public sealed class ConcordatViolationException : Exception
{
    /// <summary>Creates a violation.</summary>
    /// <param name="code">A stable <c>concordatCode</c>.</param>
    /// <param name="message">What was wrong.</param>
    /// <param name="subject">The subject, when known.</param>
    /// <param name="schemaId">The schema id, when known.</param>
    public ConcordatViolationException(string code, string message, string? subject = null, string? schemaId = null)
        : base(message)
    {
        Code = code;
        Subject = subject;
        SchemaId = schemaId;
    }

    /// <summary>Creates a violation with no detail. Prefer the overload that carries a code.</summary>
    public ConcordatViolationException()
        : this("payload_invalid", "A message did not conform to its schema.")
    {
    }

    /// <summary>Creates a violation.</summary>
    /// <param name="message">What was wrong.</param>
    public ConcordatViolationException(string message)
        : this("payload_invalid", message)
    {
    }

    /// <summary>Creates a violation.</summary>
    /// <param name="message">What was wrong.</param>
    /// <param name="innerException">The cause.</param>
    public ConcordatViolationException(string message, Exception innerException)
        : base(message, innerException) => Code = "payload_invalid";

    /// <summary>The stable <c>concordatCode</c>, so callers branch on a token not a message.</summary>
    public string Code { get; } = string.Empty;

    /// <summary>The subject, when known.</summary>
    public string? Subject { get; }

    /// <summary>The schema id, when known.</summary>
    public string? SchemaId { get; }
}
