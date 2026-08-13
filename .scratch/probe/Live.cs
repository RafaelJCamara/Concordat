using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

internal static class Live
{
    public static async Task RunAsync(string uri)
    {
        var factory = new ConnectionFactory { Uri = new Uri(uri) };
        await using var conn = await factory.CreateConnectionAsync();
        await using var ch = await conn.CreateChannelAsync();

        Console.WriteLine("=== LIVE BROKER: connected. ServerProperties product/version ===");
        foreach (var kv in conn.ServerProperties)
        {
            if (kv.Key is "product" or "version" or "platform")
                Console.WriteLine($"  {kv.Key} = {(kv.Value is byte[] b ? Encoding.UTF8.GetString(b) : kv.Value)}  (CLR {(kv.Value?.GetType().Name ?? "null")})");
        }
        Console.WriteLine();

        var q = await ch.QueueDeclareAsync(queue: "concordat-probe", durable: true, exclusive: false, autoDelete: true);
        Console.WriteLine($"  queue = {q.QueueName}");
        Console.WriteLine();

        // ---------- 1. Header CLR-type round trip through a real broker ----------
        Console.WriteLine("=== 1. HEADER ROUND TRIP THROUGH REAL BROKER ===");
        var headers = new Dictionary<string, object?>
        {
            ["concordat-v"] = "1",
            ["concordat-schema-id"] = "7f3a9c2ea1b84d5c9e07f2b3c4d5e6b4",
            ["h-bytes"] = new byte[] { 0x41, 0x42 },
            ["h-bool-true"] = true,
            ["h-bool-false"] = false,
            ["h-int"] = 42,
            ["h-long"] = 42L,
            ["h-short"] = (short)42,
            ["h-byte"] = (byte)42,
            ["h-sbyte"] = (sbyte)42,
            ["h-uint"] = 42u,
            ["h-ushort"] = (ushort)42,
            ["h-float"] = 4.5f,
            ["h-double"] = 4.5d,
            ["h-decimal"] = 4.5m,
            ["h-null"] = null,
            ["h-timestamp"] = new AmqpTimestamp(1700000000),
            ["h-list"] = new List<object?> { "a", 1 },
            ["h-nested"] = new Dictionary<string, object?> { ["inner"] = "v" },
            ["h-empty-string"] = "",
        };

        var props = new BasicProperties
        {
            Type = "acme.orders.OrderCreated",
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            AppId = "probe",
            MessageId = "m-1",
            Persistent = false,
            Headers = headers,
        };

        await ch.BasicPublishAsync(exchange: "", routingKey: q.QueueName, mandatory: false,
            basicProperties: props, body: Encoding.UTF8.GetBytes("{\"ok\":true}"));

        BasicGetResult? got = null;
        for (int i = 0; i < 50 && got is null; i++) { got = await ch.BasicGetAsync(q.QueueName, autoAck: true); if (got is null) await Task.Delay(100); }
        if (got is null) { Console.WriteLine("  !! no message"); return; }

        Console.WriteLine($"  properties.Type        = \"{got.BasicProperties.Type}\"        CLR={got.BasicProperties.Type?.GetType().Name}");
        Console.WriteLine($"  properties.ContentType = \"{got.BasicProperties.ContentType}\" CLR={got.BasicProperties.ContentType?.GetType().Name}");
        Console.WriteLine($"  properties CLR type    = {got.BasicProperties.GetType().FullName}");
        Console.WriteLine($"  Headers CLR type       = {got.BasicProperties.Headers?.GetType().FullName}");
        Console.WriteLine("  --- header values as received ---");
        foreach (var kv in got.BasicProperties.Headers!)
        {
            string v = kv.Value switch
            {
                null => "null",
                byte[] bb => $"byte[{bb.Length}] utf8=\"{Encoding.UTF8.GetString(bb)}\"",
                System.Collections.IDictionary d => $"IDictionary(count={d.Count})",
                System.Collections.IList l => $"IList(count={l.Count})",
                _ => kv.Value.ToString() ?? "?"
            };
            Console.WriteLine($"    {kv.Key,-22} -> CLR {(kv.Value?.GetType().Name ?? "null"),-22} value {v}");
        }
        Console.WriteLine();

        // ---------- 2. Illegal header CLR types ----------
        Console.WriteLine("=== 2. ILLEGAL HEADER CLR TYPES (client-side) ===");
        foreach (object? bad in new object?[] { 7UL, DateTime.UnixEpoch, Guid.Empty, 'c', TimeSpan.Zero, new Uri("http://x/") })
        {
            var p2 = new BasicProperties { Headers = new Dictionary<string, object?> { ["k"] = bad } };
            try
            {
                await ch.BasicPublishAsync("", q.QueueName, false, p2, ReadOnlyMemory<byte>.Empty);
                Console.WriteLine($"  {bad!.GetType().Name,-12} -> ACCEPTED");
                await ch.BasicGetAsync(q.QueueName, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {bad!.GetType().Name,-12} -> {ex.GetType().Name}: {ex.Message}");
                if (ch.IsClosed) { Console.WriteLine("     !! channel closed by the failure"); break; }
            }
        }
        Console.WriteLine($"  channel still open after illegal-type attempts: {ch.IsOpen}");
        Console.WriteLine();

        // ---------- 3. Consumer throw -> CallbackExceptionAsync ----------
        Console.WriteLine("=== 3. EXCEPTION THROWN FROM CONSUMER CALLBACK ===");
        await using var ch2 = await conn.CreateChannelAsync();
        var q2 = await ch2.QueueDeclareAsync("concordat-probe-2", true, false, true);
        await ch2.BasicQosAsync(0, 10, false);

        var callbackExceptions = new List<CallbackExceptionEventArgs>();
        var sawCallbackException = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ch2.CallbackExceptionAsync += (_, ea) =>
        {
            callbackExceptions.Add(ea);
            sawCallbackException.TrySetResult();
            return Task.CompletedTask;
        };

        int delivered = 0;
        var deliveredBoth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new AsyncEventingBasicConsumer(ch2);
        consumer.ReceivedAsync += (_, ea) =>
        {
            int n = Interlocked.Increment(ref delivered);
            Console.WriteLine($"    delivery #{n} tag={ea.DeliveryTag} body={Encoding.UTF8.GetString(ea.Body.Span)}");
            if (n == 2) deliveredBoth.TrySetResult();
            throw new InvalidOperationException($"schema violation on delivery #{n}");
        };

        string tag = await ch2.BasicConsumeAsync(q2.QueueName, autoAck: false, consumer: consumer);
        Console.WriteLine($"  consumerTag = {tag}");

        await ch2.BasicPublishAsync("", q2.QueueName, false, new BasicProperties(), Encoding.UTF8.GetBytes("one"));
        await ch2.BasicPublishAsync("", q2.QueueName, false, new BasicProperties(), Encoding.UTF8.GetBytes("two"));

        await Task.WhenAny(deliveredBoth.Task, Task.Delay(5000));
        await Task.Delay(500);

        Console.WriteLine($"  deliveries seen after 2 throws: {delivered}   (dispatch loop continued? {delivered == 2})");
        Console.WriteLine($"  channel IsOpen after throws: {ch2.IsOpen}");
        Console.WriteLine($"  CallbackExceptionAsync fired {callbackExceptions.Count} time(s)");
        foreach (var ea in callbackExceptions)
        {
            Console.WriteLine($"    Exception: {ea.Exception.GetType().Name}: {ea.Exception.Message}");
            foreach (var kv in ea.Detail) Console.WriteLine($"      Detail[{kv.Key}] = {kv.Value?.GetType().Name} {kv.Value}");
        }
        Console.WriteLine($"  consumer.IsRunning = {consumer.IsRunning}");
        Console.WriteLine($"  messages still unacked (MessageCountAsync counts only ready): ready={await ch2.MessageCountAsync(q2.QueueName)}, consumers={await ch2.ConsumerCountAsync(q2.QueueName)}");
        Console.WriteLine("  -> the two deliveries were neither acked nor nacked; they stay unacked until channel/connection close.");
        Console.WriteLine();

        // ---------- 4. Nack path ----------
        Console.WriteLine("=== 4. NACK FROM INSIDE THE CONSUMER CALLBACK ===");
        await using var ch3 = await conn.CreateChannelAsync();
        var q3 = await ch3.QueueDeclareAsync("concordat-probe-3", true, false, true);
        var nacked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var c3 = new AsyncEventingBasicConsumer(ch3);
        c3.ReceivedAsync += async (_, ea) =>
        {
            // ea.Channel is the IAsyncBasicConsumer.Channel; nack lives on IChannel.
            await ch3.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ea.CancellationToken);
            Console.WriteLine($"    nacked tag={ea.DeliveryTag} requeue:false");
            nacked.TrySetResult();
        };
        await ch3.BasicConsumeAsync(q3.QueueName, autoAck: false, consumer: c3);
        await ch3.BasicPublishAsync("", q3.QueueName, false, new BasicProperties(), Encoding.UTF8.GetBytes("nackme"));
        await Task.WhenAny(nacked.Task, Task.Delay(5000));
        await Task.Delay(300);
        Console.WriteLine($"  ready after nack(requeue:false) = {await ch3.MessageCountAsync(q3.QueueName)} (0 => dropped/dead-lettered, not requeued)");
        Console.WriteLine($"  BasicDeliverEventArgs exposes: Channel={c3.Channel?.GetType().Name}, and ea.Channel exists? see dump below");
        Console.WriteLine();

        // ---------- 5. Decorator viability: swapping TProperties ----------
        Console.WriteLine("=== 5. DECORATOR: rewriting TProperties on the way through ===");
        await using var ch4Inner = await conn.CreateChannelAsync();
        var q4 = await ch4Inner.QueueDeclareAsync("concordat-probe-4", true, false, true);
        IChannel ch4 = new DecoratingChannel(ch4Inner);

        // (a) caller went through the extension overload => TProperties is the INTERNAL EmptyBasicProperty
        await ch4.BasicPublishAsync(exchange: "", routingKey: q4.QueueName, body: Encoding.UTF8.GetBytes("{}"));
        var r4a = await Drain(ch4Inner, q4.QueueName);
        Dump("(a) via extension overload, TProperties=EmptyBasicProperty", r4a);

        // (b) caller supplied their own BasicProperties
        await ch4.BasicPublishAsync("", q4.QueueName, false,
            new BasicProperties { Type = "acme.orders.OrderCreated", ContentType = "application/json" },
            Encoding.UTF8.GetBytes("{}"));
        var r4b = await Drain(ch4Inner, q4.QueueName);
        Dump("(b) caller-supplied BasicProperties", r4b);

        // (c) caller supplied the READ-ONLY struct they got off a delivery (forwarding scenario)
        IReadOnlyBasicProperties incoming = r4b!.BasicProperties;
        await ch4.BasicPublishAsync("", q4.QueueName, false, new BasicProperties(incoming), Encoding.UTF8.GetBytes("{}"));
        var r4c = await Drain(ch4Inner, q4.QueueName);
        Dump("(c) re-wrapped ReadOnlyBasicProperties", r4c);
        Console.WriteLine();

        static async Task<BasicGetResult?> Drain(IChannel ch, string queue)
        {
            for (int i = 0; i < 50; i++)
            {
                var r = await ch.BasicGetAsync(queue, autoAck: true);
                if (r is not null) return r;
                await Task.Delay(50);
            }
            return null;
        }

        static void Dump(string label, BasicGetResult? r)
        {
            if (r is null) { Console.WriteLine($"  {label}: NO MESSAGE"); return; }
            var hs = r.BasicProperties.Headers is null
                ? "<none>"
                : string.Join(", ", System.Linq.Enumerable.Select(r.BasicProperties.Headers,
                    kv => kv.Key + "=" + (kv.Value is byte[] b ? "\"" + Encoding.UTF8.GetString(b) + "\"" : kv.Value?.ToString() ?? "null")));
            Console.WriteLine($"  {label}");
            Console.WriteLine($"      Type=\"{r.BasicProperties.Type}\" ContentType=\"{r.BasicProperties.ContentType}\" headers: {hs}");
        }
    }
}

/// A minimal Concordat-shaped publish decorator: it must inject headers into a
/// TProperties it does not own and whose concrete type it may not be able to name.
internal sealed class DecoratingChannel(IChannel inner) : IChannel
{
    public ValueTask BasicPublishAsync<TProperties>(string exchange, string routingKey, bool mandatory,
        TProperties basicProperties, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
        where TProperties : IReadOnlyBasicProperties, IAmqpHeader
    {
        Console.WriteLine($"      [decorator] TProperties = {typeof(TProperties).FullName} (public={typeof(TProperties).IsPublic})");
        var rewritten = new BasicProperties(basicProperties)
        {
            Headers = new Dictionary<string, object?>(basicProperties.Headers ?? new Dictionary<string, object?>())
            {
                ["concordat-v"] = "1",
                ["concordat-schema-id"] = "7f3a9c2ea1b84d5c9e07f2b3c4d5e6b4",
            }
        };
        rewritten.Type ??= "unresolved";
        return inner.BasicPublishAsync(exchange, routingKey, mandatory, rewritten, body, cancellationToken);
    }

    public ValueTask BasicPublishAsync<TProperties>(CachedString exchange, CachedString routingKey, bool mandatory,
        TProperties basicProperties, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
        where TProperties : IReadOnlyBasicProperties, IAmqpHeader
        => BasicPublishAsync(exchange.Value, routingKey.Value, mandatory, basicProperties, body, cancellationToken);

    // --- everything else is straight delegation ---
    public int ChannelNumber => inner.ChannelNumber;
    public ShutdownEventArgs? CloseReason => inner.CloseReason;
    public IAsyncBasicConsumer? DefaultConsumer { get => inner.DefaultConsumer; set => inner.DefaultConsumer = value; }
    public bool IsClosed => inner.IsClosed;
    public bool IsOpen => inner.IsOpen;
    public string? CurrentQueue => inner.CurrentQueue;
    public TimeSpan ContinuationTimeout { get => inner.ContinuationTimeout; set => inner.ContinuationTimeout = value; }
    public event AsyncEventHandler<BasicAckEventArgs> BasicAcksAsync { add => inner.BasicAcksAsync += value; remove => inner.BasicAcksAsync -= value; }
    public event AsyncEventHandler<BasicNackEventArgs> BasicNacksAsync { add => inner.BasicNacksAsync += value; remove => inner.BasicNacksAsync -= value; }
    public event AsyncEventHandler<BasicReturnEventArgs> BasicReturnAsync { add => inner.BasicReturnAsync += value; remove => inner.BasicReturnAsync -= value; }
    public event AsyncEventHandler<CallbackExceptionEventArgs> CallbackExceptionAsync { add => inner.CallbackExceptionAsync += value; remove => inner.CallbackExceptionAsync -= value; }
    public event AsyncEventHandler<FlowControlEventArgs> FlowControlAsync { add => inner.FlowControlAsync += value; remove => inner.FlowControlAsync -= value; }
    public event AsyncEventHandler<ShutdownEventArgs> ChannelShutdownAsync { add => inner.ChannelShutdownAsync += value; remove => inner.ChannelShutdownAsync -= value; }
    public ValueTask BasicAckAsync(ulong t, bool m, CancellationToken c = default) => inner.BasicAckAsync(t, m, c);
    public Task BasicCancelAsync(string t, bool n = false, CancellationToken c = default) => inner.BasicCancelAsync(t, n, c);
    public Task<string> BasicConsumeAsync(string q, bool a, string t, bool nl, bool e, IDictionary<string, object?>? args, IAsyncBasicConsumer con, CancellationToken c = default) => inner.BasicConsumeAsync(q, a, t, nl, e, args, con, c);
    public Task<BasicGetResult?> BasicGetAsync(string q, bool a, CancellationToken c = default) => inner.BasicGetAsync(q, a, c);
    public ValueTask BasicNackAsync(ulong t, bool m, bool r, CancellationToken c = default) => inner.BasicNackAsync(t, m, r, c);
    public Task BasicQosAsync(uint s, ushort n, bool g, CancellationToken c = default) => inner.BasicQosAsync(s, n, g, c);
    public ValueTask BasicRejectAsync(ulong t, bool r, CancellationToken c = default) => inner.BasicRejectAsync(t, r, c);
    public Task CloseAsync(ushort rc, string rt, bool a, CancellationToken c = default) => inner.CloseAsync(rc, rt, a, c);
    public Task CloseAsync(ShutdownEventArgs r, bool a) => inner.CloseAsync(r, a);
    public Task CloseAsync(ShutdownEventArgs r, bool a, CancellationToken c) => inner.CloseAsync(r, a, c);
    public Task<uint> ConsumerCountAsync(string q, CancellationToken c = default) => inner.ConsumerCountAsync(q, c);
    public Task ExchangeBindAsync(string d, string s, string rk, IDictionary<string, object?>? a = null, bool nw = false, CancellationToken c = default) => inner.ExchangeBindAsync(d, s, rk, a, nw, c);
    public Task ExchangeDeclareAsync(string ex, string ty, bool du, bool ad, IDictionary<string, object?>? a = null, bool p = false, bool nw = false, CancellationToken c = default) => inner.ExchangeDeclareAsync(ex, ty, du, ad, a, p, nw, c);
    public Task ExchangeDeclarePassiveAsync(string e, CancellationToken c = default) => inner.ExchangeDeclarePassiveAsync(e, c);
    public Task ExchangeDeleteAsync(string e, bool iu = false, bool nw = false, CancellationToken c = default) => inner.ExchangeDeleteAsync(e, iu, nw, c);
    public Task ExchangeUnbindAsync(string d, string s, string rk, IDictionary<string, object?>? a = null, bool nw = false, CancellationToken c = default) => inner.ExchangeUnbindAsync(d, s, rk, a, nw, c);
    public ValueTask<ulong> GetNextPublishSequenceNumberAsync(CancellationToken c = default) => inner.GetNextPublishSequenceNumberAsync(c);
    public Task<uint> MessageCountAsync(string q, CancellationToken c = default) => inner.MessageCountAsync(q, c);
    public Task QueueBindAsync(string q, string e, string rk, IDictionary<string, object?>? a = null, bool nw = false, CancellationToken c = default) => inner.QueueBindAsync(q, e, rk, a, nw, c);
    public Task<QueueDeclareOk> QueueDeclareAsync(string q, bool d, bool ex, bool ad, IDictionary<string, object?>? a = null, bool p = false, bool nw = false, CancellationToken c = default) => inner.QueueDeclareAsync(q, d, ex, ad, a, p, nw, c);
    public Task<QueueDeclareOk> QueueDeclarePassiveAsync(string q, CancellationToken c = default) => inner.QueueDeclarePassiveAsync(q, c);
    public Task<uint> QueueDeleteAsync(string q, bool iu, bool ie, bool nw = false, CancellationToken c = default) => inner.QueueDeleteAsync(q, iu, ie, nw, c);
    public Task<uint> QueuePurgeAsync(string q, CancellationToken c = default) => inner.QueuePurgeAsync(q, c);
    public Task QueueUnbindAsync(string q, string e, string rk, IDictionary<string, object?>? a = null, CancellationToken c = default) => inner.QueueUnbindAsync(q, e, rk, a, c);
    public Task TxCommitAsync(CancellationToken c = default) => inner.TxCommitAsync(c);
    public Task TxRollbackAsync(CancellationToken c = default) => inner.TxRollbackAsync(c);
    public Task TxSelectAsync(CancellationToken c = default) => inner.TxSelectAsync(c);
    public void Dispose() => inner.Dispose();
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
