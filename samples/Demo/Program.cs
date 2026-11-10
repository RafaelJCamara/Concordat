// Concordat demo -- a small shop whose services talk over RabbitMQ, with contracts in the registry.
//
// It seeds the registry, then keeps four services running until you press Ctrl+C:
//
//   checkout  --demo.orders/orders.placed------>  billing     contract demo-orders    ENFORCE
//   gateway   --demo.payments/payments.captured->  ledger      contract demo-payments  MONITOR
//
// Every few messages one of them is wrong on purpose. Under ENFORCE the publisher is refused before the
// broker sees the message; under MONITOR the message goes out and is counted. Either way the services
// report it to the registry, so it shows up in the web app: Dashboard, Contracts, Approvals.
//
// Prerequisites (see docs/DEMO.md): the registry and RabbitMQ running, and an API key in CONCORDAT_API_KEY
// (or a registry started with the anonymous opt-in). Options:
//   --interval <ms>   time between rounds (default 1500)
//   --count <n>       stop after n rounds instead of running until Ctrl+C
//   --seed-only       prepare the registry and exit
//   --currency        make checkout send "currency" on every order: the "fixed" checkout for when you have
//                     approved the held change that makes it required

using System.Net;
using Concordat.Client;
using Concordat.RabbitMq;
using Demo;
using RabbitMQ.Client;

var interval = TimeSpan.FromMilliseconds(IntOption("--interval") ?? 1500);
var rounds = IntOption("--count");
var seedOnly = args.Contains("--seed-only");
var currency = args.Contains("--currency") ? "EUR" : null;

var settings = Settings.FromEnvironment();

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

try
{
    await RunAsync();
    return 0;
}
catch (DemoException ex)
{
    Console.Error.WriteLine($"\n{ex.Message}");
    return 1;
}
catch (ConcordatException ex)
{
    // The client refused to start unenforced (RequireWarmUp), which is what it is for.
    Console.Error.WriteLine($"\nThe Concordat client could not start: {ex.Message}");
    return 1;
}

async Task RunAsync()
{
    Log.Plain($"Concordat demo: registry {settings.Registry}, environment '{settings.Environment}', " +
              $"broker {settings.BrokerHost}:{settings.BrokerPort}\n");

    await RequireRegistryAsync();

    using (var http = settings.RegistryHttp())
    {
        await new Seed(http, settings.Environment).RunAsync();
    }

    if (seedOnly)
    {
        Log.Plain("Seeded. Open the web app: the Dashboard, Contracts and Approvals pages now have something on them.");
        return;
    }

    await using var connection = await ConnectAsync();

    // Topology is the application's to declare, not Concordat's. A contract names an exchange and a queue; it
    // does not create them.
    await using (var setup = await connection.CreateChannelAsync())
    {
        await Declare(setup, Names.OrdersExchange, Names.BillingQueue, "orders.#");
        await Declare(setup, Names.PaymentsExchange, Names.LedgerQueue, "payments.#");
    }

    var orders = new[] { new PublishRoute(Names.OrdersExchange, Names.OrdersRoutingKey) };
    var payments = new[] { new PublishRoute(Names.PaymentsExchange, Names.PaymentsRoutingKey) };

    await using var checkout = await DemoService.StartAsync(
        "checkout", settings, connection, [Names.OrderPlaced], [], orders, []);
    await using var billing = await DemoService.StartAsync(
        "billing", settings, connection, [], [Names.OrderPlaced], [], [Names.BillingQueue]);
    await using var gateway = await DemoService.StartAsync(
        "gateway", settings, connection, [Names.PaymentCaptured], [], payments, []);
    await using var ledger = await DemoService.StartAsync(
        "ledger", settings, connection, [], [Names.PaymentCaptured], [], [Names.LedgerQueue]);

    DemoService[] services = [checkout, billing, gateway, ledger];

    await billing.ConsumeAsync(Names.BillingQueue, d => Describe("order", d.Body));
    await ledger.ConsumeAsync(Names.LedgerQueue, d => Describe("payment", d.Body));

    Log.Plain("Four services are running. Messages follow; a violation is reported to the registry every few seconds.");
    Log.Plain("Ctrl+C to stop.\n");

    // ---------------------------------------------------------------------------------- traffic
    for (var n = 1; rounds is null || n <= rounds; n++)
    {
        await checkout.PublishAsync(
            Names.OrdersExchange,
            Names.OrdersRoutingKey,
            Names.OrderPlaced,
            $"order ord-{n:0000}",
            Traffic.Order(n, currency));

        await gateway.PublishAsync(
            Names.PaymentsExchange,
            Names.PaymentsRoutingKey,
            Names.PaymentCaptured,
            $"payment pay-{n:0000}",
            Traffic.Payment(n));

        try
        {
            await Task.Delay(interval, stopping.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }

    // Let the consumers finish what is already queued before the summary is printed.
    await Task.Delay(TimeSpan.FromMilliseconds(500));

    foreach (var service in services)
    {
        await service.StopAsync();
    }

    Summary(services);
}

// ------------------------------------------------------------------------------------- helpers

int? IntOption(string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var value) ? value : null;
}

async Task RequireRegistryAsync()
{
    using var http = settings.RegistryHttp();

    try
    {
        using var ready = await http.GetAsync("/health/ready");
        ready.EnsureSuccessStatusCode();
    }
    catch (HttpRequestException ex)
    {
        throw new DemoException(
            $"The registry is not answering at {settings.Registry} ({ex.Message}).\n" +
            "Start it first (docs/DEMO.md step 1) or point CONCORDAT_URL at it.");
    }

    // A new registry answers nobody until it is claimed (ADR-047), and the demo cannot sensibly claim an instance
    // on somebody's behalf. Say what to do instead of failing on the first write.
    using var status = await http.GetAsync("/v1/auth/status");

    if (status.StatusCode == HttpStatusCode.OK && settings.ApiKey is null)
    {
        var body = await status.Content.ReadAsStringAsync();

        if (body.Contains("\"claimed\":false", StringComparison.Ordinal) ||
            body.Contains("\"authenticated\":false", StringComparison.Ordinal))
        {
            throw new DemoException(
                "CONCORDAT_API_KEY is not set, and this registry will not answer without a credential.\n" +
                "  1. Open the web app and create the first owner (an unclaimed registry shows a setup screen).\n" +
                "  2. Settings > API keys: create a key that holds subject:write and contract:write.\n" +
                "  3. Run the demo again with CONCORDAT_API_KEY=<the key>.\n" +
                "docs/DEMO.md has the whole story.");
        }
    }
}

async Task<IConnection> ConnectAsync()
{
    try
    {
        return await new ConnectionFactory
        {
            HostName = settings.BrokerHost,
            Port = settings.BrokerPort,
            UserName = settings.BrokerUser,
            Password = settings.BrokerPassword,
        }.CreateConnectionAsync();
    }
    catch (Exception ex) when (ex is RabbitMQ.Client.Exceptions.BrokerUnreachableException or
                                   System.Net.Sockets.SocketException or
                                   RabbitMQ.Client.Exceptions.AuthenticationFailureException)
    {
        throw new DemoException(
            $"Could not connect to RabbitMQ at {settings.BrokerHost}:{settings.BrokerPort} ({ex.Message}).\n" +
            "Start it with the compose file (docs/DEMO.md step 1), or set RABBITMQ_HOST, RABBITMQ_PORT, " +
            "RABBITMQ_USER and RABBITMQ_PASSWORD.");
    }
}

static async Task Declare(IChannel channel, string exchange, string queue, string binding)
{
    await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true);
    await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
    await channel.QueueBindAsync(queue, exchange, binding);
}

static string Describe(string noun, ReadOnlyMemory<byte> body)
{
    // Reads the id back out of the payload for the log line. A payload that is not JSON is described as it is.
    try
    {
        using var json = System.Text.Json.JsonDocument.Parse(body);
        var root = json.RootElement;
        var key = noun == "order" ? "orderId" : "paymentId";

        return root.TryGetProperty(key, out var id) ? $"{noun} {id}".Replace("\"", string.Empty) : $"{noun} (no id)";
    }
    catch (System.Text.Json.JsonException)
    {
        return $"{noun} (not JSON)";
    }
}

static void Summary(DemoService[] services)
{
    Log.Plain("\nWhat each service saw:");

    foreach (var service in services)
    {
        var c = service.Observer.Counters;

        long Both(EnforcementOutcome outcome) =>
            c.Count(EnforcementSide.Publish, outcome) + c.Count(EnforcementSide.Consume, outcome);

        Log.Plain(
            $"  {service.Name,-9} valid {Both(EnforcementOutcome.Valid),3}" +
            $"   refused {Both(EnforcementOutcome.Blocked),3}" +
            $"   broke the contract but let through {Both(EnforcementOutcome.Observed),3}");
    }

    Log.Plain("\nThe registry never sees the messages themselves, only what the services report:");
    Log.Plain("  Dashboard  - violations, held breaking changes, contracts by enforcement mode");
    Log.Plain("  Contracts  - demo-orders (ENFORCE) and demo-payments (MONITOR) and what they bind");
    Log.Plain("  Approvals  - the breaking change to OrderPlaced waiting for a decision");
}
