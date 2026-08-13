// Concordat quickstart — publish a message that satisfies its contract, then one that does
// not, and watch the difference.
//
// Prerequisites (see docs/QUICKSTART.md):
//   1. docker compose -f deploy/compose/docker-compose.yml up -d
//   2. the registry running on http://localhost:5062
//
// Everything below uses only the published SDK surface. Nothing reaches into Concordat's
// internals, because the whole point of ADR-019 is that it does not have to.

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Concordat.Client;
using Concordat.Domain.Registry;
using Concordat.Formats.Json;
using Concordat.RabbitMq;
using RabbitMQ.Client;

const string Registry = "http://localhost:5062";
const string Environment = "dev";
const string Subject = "acme.orders.OrderCreated";
const string Exchange = "quickstart";
const string Queue = "quickstart.orders";

// The contract. 'orderId' is required and 'total' must be a number.
const string Schema = """
{
  "type": "object",
  "properties": {
    "orderId": { "type": "string" },
    "total": { "type": "number" }
  },
  "required": ["orderId"]
}
""";

using var http = new HttpClient { BaseAddress = new Uri(Registry) };

// ---------------------------------------------------------------- 1. register the contract
//
// Registration goes over plain REST, not through the SDK client. That is deliberate: under
// ADR-005 registration belongs to CI and the CLI, and the runtime client is read-only, so a
// misbehaving producer cannot invent a contract for itself on the way past.
//
// In a real repository you would run `concordat push` from CI instead of this.

Console.WriteLine($"→ registering {Subject} in '{Environment}'");

await http.PostAsJsonAsync(
    $"/v1/environments/{Environment}/subjects",
    new { name = Subject, format = "json", owner = "quickstart" });

var registration = await http.PostAsJsonAsync(
    $"/v1/environments/{Environment}/subjects/{Subject}/versions",
    new { schema = Schema, registeredBy = "quickstart" });

registration.EnsureSuccessStatusCode();

using (var body = JsonDocument.Parse(await registration.Content.ReadAsStringAsync()))
{
    var root = body.RootElement;
    Console.WriteLine($"  schema id {root.GetProperty("schemaId").GetString()}");

    // M6.1: anything here will not behave identically in every SDK. Empty for this schema.
    foreach (var finding in root.GetProperty("portability").EnumerateArray())
    {
        Console.WriteLine($"  ⚠ {finding.GetProperty("kind").GetString()}: " +
                          finding.GetProperty("message").GetString());
    }
}

// ------------------------------------------------------------------- 2. build the SDK client
//
// The client caches schemas forever (they are content-addressed, so they cannot change under
// you) and the 'latest' pointer for 30 seconds. WarmUpAsync pre-loads the environment in one
// request rather than N — cold start is the real load pattern, not steady state.

var client = new ConcordatClient(
    new HttpClient { BaseAddress = new Uri(Registry) },
    new ConcordatClientOptions
    {
        BaseAddress = new Uri(Registry),
        Environment = Environment,

        // FailOpen is the default: if the registry is unreachable, keep delivering and record
        // it. Switch to FailClosed where an unvalidated message is worse than a delayed one.
        OnResolutionFailure = ResolutionFailureMode.FailOpen,
    });

var status = await client.WarmUpAsync();
Console.WriteLine($"→ warmed up: {status}");

// --------------------------------------------------------------------- 3. wrap the channel

var connection = await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync();
var raw = await connection.CreateChannelAsync();

await raw.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true);
await raw.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false);
await raw.QueueBindAsync(Queue, Exchange, "#");

var options = new ConcordatRabbitMqOptions
{
    // Monitor is the default -- adding a package reference must never start rejecting
    // production traffic. Enforce is what you want once you trust it, and it is what makes
    // the second publish below actually fail.
    Mode = EnforcementMode.Enforce,
};

var enforcer = new SchemaEnforcer(client, options, [new NJsonSchemaPayloadValidator()]);

// ConcordatChannel decorates IChannel completely, so every publish path is covered. An
// opt-in `PublishValidated()` extension would leave every other BasicPublishAsync unenforced.
var channel = new ConcordatChannel(raw, enforcer, options);

// ------------------------------------------------------------------------ 4. publish

await Publish(channel, "a valid order", new { orderId = "ord-1", total = 42.5 });

// 'total' is a string where the contract says number, so this one is refused.
await Publish(channel, "an invalid order", new { orderId = "ord-2", total = "not-a-number" });

// ------------------------------------------------------------------------ 5. consume

Console.WriteLine("\n→ draining the queue");

while (await raw.BasicGetAsync(Queue, autoAck: true) is { } delivery)
{
    var text = Encoding.UTF8.GetString(delivery.Body.Span);

    // The envelope the publisher stamped. A consumer knows which exact schema validated this
    // message without asking anyone -- the id is in the headers and is content-addressed.
    var schemaId = Header(delivery.BasicProperties.Headers, "concordat-schema-id");

    Console.WriteLine($"  {text}");
    Console.WriteLine($"    schema {schemaId ?? "(no envelope)"}");
}

await channel.CloseAsync();
await connection.CloseAsync();

Console.WriteLine("\nDone. Nothing invalid reached the queue.");

// ---------------------------------------------------------------------------- helpers

static async Task Publish(ConcordatChannel channel, string label, object payload)
{
    var body = JsonSerializer.SerializeToUtf8Bytes(payload);

    // The subject comes from properties.Type by default (ADR-011: the subject is the message
    // type, not the routing key -- publisher and consumer name different things, and only the
    // type is known to both).
    var properties = new BasicProperties { Type = Subject, ContentType = "application/json" };

    Console.WriteLine($"\n→ publishing {label}");

    try
    {
        await channel.BasicPublishAsync(
            Exchange, "orders.created", mandatory: false, properties, body);

        Console.WriteLine("  accepted");
    }
    catch (ConcordatViolationException ex)
    {
        // Under Mode = Enforce the publish is refused before it reaches the broker, so the
        // bad message never exists. Under Monitor it would be published and counted instead.
        Console.WriteLine($"  refused: {ex.Message}");
    }
}

static string? Header(IDictionary<string, object?>? headers, string name) =>
    headers is not null && headers.TryGetValue(name, out var value) && value is byte[] bytes
        ? Encoding.UTF8.GetString(bytes)
        : null;
