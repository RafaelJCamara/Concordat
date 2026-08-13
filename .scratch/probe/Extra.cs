using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

internal static class Extra
{
    public static async Task RunAsync(string uri)
    {
        var asm = typeof(IChannel).Assembly;

        Console.WriteLine("=== FIELDS (these types expose data as public fields, not properties) ===");
        foreach (var tn in new[]
        {
            "RabbitMQ.Client.Events.BasicDeliverEventArgs",
            "RabbitMQ.Client.CachedString",
            "RabbitMQ.Client.BasicGetResult",
            "RabbitMQ.Client.Events.AsyncEventArgs",
            "RabbitMQ.Client.Events.BaseExceptionEventArgs",
            "RabbitMQ.Client.Events.CallbackExceptionEventArgs",
        })
        {
            var t = asm.GetType(tn)!;
            Console.WriteLine($"  {tn}");
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.Name))
                Console.WriteLine($"      field  {f.FieldType.Name} {f.Name}{(f.IsInitOnly ? " (readonly)" : "")}");
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name))
                Console.WriteLine($"      prop   {p.PropertyType.Name} {p.Name}");
            foreach (var c in t.GetConstructors())
                Console.WriteLine($"      ctor   ({string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
        }
        Console.WriteLine();

        Console.WriteLine("=== IConnection.CallbackExceptionAsync present? ===");
        Console.WriteLine("  " + string.Join(", ", typeof(IConnection).GetEvents().Select(e => e.Name)));
        Console.WriteLine();

        Console.WriteLine("=== CreateChannelOptions (publisher confirms) ===");
        var cco = asm.GetType("RabbitMQ.Client.CreateChannelOptions");
        if (cco is not null)
        {
            foreach (var p in cco.GetProperties()) Console.WriteLine($"  prop {p.PropertyType.Name} {p.Name}");
            foreach (var f in cco.GetFields(BindingFlags.Public | BindingFlags.Static)) Console.WriteLine($"  static {f.FieldType.Name} {f.Name}");
            foreach (var c in cco.GetConstructors()) Console.WriteLine($"  ctor ({string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name + (p.HasDefaultValue ? " = " + p.RawDefaultValue : "")))})");
        }
        Console.WriteLine();

        var factory = new ConnectionFactory { Uri = new Uri(uri) };
        await using var conn = await factory.CreateConnectionAsync();

        Console.WriteLine("=== 6. A THROW IN THE PUBLISH DECORATOR BLOCKS THE PUBLISH ===");
        await using var ch = await conn.CreateChannelAsync();
        var q = await ch.QueueDeclareAsync("concordat-probe-6", true, false, true);
        try
        {
            await new BlockingChannel(ch).BasicPublishAsync("", q.QueueName, false, new BasicProperties(), Encoding.UTF8.GetBytes("nope"));
            Console.WriteLine("  !! no throw");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  caller observed {ex.GetType().Name}: {ex.Message}");
        }
        await Task.Delay(300);
        Console.WriteLine($"  messages in queue = {await ch.MessageCountAsync(q.QueueName)}  (0 => the publish never reached the broker)");
        Console.WriteLine();

        Console.WriteLine("=== 7. PUBLISHER CONFIRMS: does BasicPublishAsync throw on nack/return? ===");
        await using var chC = await conn.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        try
        {
            // mandatory publish to a routing key that binds nowhere -> basic.return
            await chC.BasicPublishAsync("", "no-such-queue-xyz", mandatory: true, new BasicProperties(), Encoding.UTF8.GetBytes("x"));
            Console.WriteLine("  mandatory unroutable publish: NO throw");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  mandatory unroutable publish -> {ex.GetType().FullName}: {ex.Message}");
        }
        Console.WriteLine();

        Console.WriteLine("=== 8. DOES THE COPY CTOR ALIAS THE CALLER'S HEADERS DICTIONARY? ===");
        var callerHeaders = new Dictionary<string, object?> { ["mine"] = "keep" };
        var callerProps = new BasicProperties { Headers = callerHeaders };
        var copy = new BasicProperties(callerProps);
        copy.Headers!["concordat-v"] = "1";
        Console.WriteLine($"  after decorator wrote into copy.Headers, caller's dictionary contains: {string.Join(", ", callerHeaders.Keys)}");
        Console.WriteLine("  -> ALIASED. A decorator MUST clone the dictionary or it mutates the caller's object.");
        Console.WriteLine();

        Console.WriteLine("=== 9. HEADER KEY LENGTH / VALUE SIZE LIMITS (client-side) ===");
        await using var ch9 = await conn.CreateChannelAsync();
        var q9 = await ch9.QueueDeclareAsync("concordat-probe-9", true, false, true);
        foreach (var (label, key, val) in new (string, string, object?)[]
        {
            ("255-char key", new string('k', 255), "v"),
            ("256-char key", new string('k', 256), "v"),
            ("64 KiB value", "big", new string('v', 65536)),
            ("1 MiB value", "big", new string('v', 1024 * 1024)),
        })
        {
            try
            {
                await ch9.BasicPublishAsync("", q9.QueueName, false,
                    new BasicProperties { Headers = new Dictionary<string, object?> { [key] = val } },
                    ReadOnlyMemory<byte>.Empty);
                var r = await ch9.BasicGetAsync(q9.QueueName, true);
                var back = r?.BasicProperties.Headers?.Values.FirstOrDefault();
                Console.WriteLine($"  {label,-14} -> OK, read back {(back is byte[] b ? $"byte[{b.Length}]" : back?.ToString() ?? "null")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {label,-14} -> {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
                if (ch9.IsClosed) { Console.WriteLine("     channel closed"); break; }
            }
        }
        Console.WriteLine();
    }
}

internal sealed class BlockingChannel(IChannel inner) : IChannel
{
    public ValueTask BasicPublishAsync<TProperties>(string exchange, string routingKey, bool mandatory,
        TProperties basicProperties, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
        where TProperties : IReadOnlyBasicProperties, IAmqpHeader
        => throw new InvalidOperationException("schema validation failed: payload does not match acme.orders.OrderCreated@3");

    public ValueTask BasicPublishAsync<TProperties>(CachedString exchange, CachedString routingKey, bool mandatory,
        TProperties basicProperties, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
        where TProperties : IReadOnlyBasicProperties, IAmqpHeader
        => throw new InvalidOperationException("schema validation failed");

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
