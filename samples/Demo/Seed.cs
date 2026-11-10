using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Demo;

/// <summary>Everything the demo is about: the names it uses, so the docs and the dashboard agree.</summary>
internal static class Names
{
    public const string OrderPlaced = "demo.orders.OrderPlaced";
    public const string PaymentCaptured = "demo.payments.PaymentCaptured";

    public const string OrdersExchange = "demo.orders";
    public const string OrdersRoutingKey = "orders.placed";
    public const string BillingQueue = "demo.billing.orders";

    public const string PaymentsExchange = "demo.payments";
    public const string PaymentsRoutingKey = "payments.captured";
    public const string LedgerQueue = "demo.ledger.payments";

    public const string OrdersContract = "demo-orders";
    public const string PaymentsContract = "demo-payments";
}

/// <summary>
/// Puts the registry into the state a team would have built up over a few weeks: two subjects with a
/// history, two contracts bound to the broker's topology, and one breaking change waiting for a decision.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registration goes over plain REST, as in the quickstart</b>, because under ADR-005 it belongs to
/// CI and the CLI and the runtime client is read-only. In a real repository this file is a
/// <c>concordat push</c> in a pipeline.
/// </para>
/// <para>
/// <b>Every step looks first and does nothing when its subject or contract is already there.</b> That is
/// not tidiness. Registering a schema that is not the tip of its subject is a new version, not a no-op,
/// so running the seed twice blindly would add a version on every run. A second run therefore leaves
/// alone whatever you did to the first (approving the held change, changing a mode).
/// </para>
/// </remarks>
internal sealed class Seed(HttpClient http, string environment)
{
    private const string Owner = "demo";

    // v1. The contract as first shipped.
    private const string OrderV1 = """
        {
          "type": "object",
          "properties": {
            "orderId":  { "type": "string" },
            "customer": { "type": "string" },
            "total":    { "type": "number", "minimum": 0 }
          },
          "required": ["orderId", "total"]
        }
        """;

    // v2. Adds an OPTIONAL field: compatible, so it becomes the live version straight away.
    private const string OrderV2 = """
        {
          "type": "object",
          "properties": {
            "orderId":  { "type": "string" },
            "customer": { "type": "string" },
            "total":    { "type": "number", "minimum": 0 },
            "coupon":   { "type": "string" }
          },
          "required": ["orderId", "total"]
        }
        """;

    // v3. Adds a REQUIRED field: every order checkout sends today would stop conforming. The registry
    // does not refuse it and does not publish it; it holds it at the approval gate until a person decides.
    private const string OrderV3Breaking = """
        {
          "type": "object",
          "properties": {
            "orderId":  { "type": "string" },
            "customer": { "type": "string" },
            "total":    { "type": "number", "minimum": 0 },
            "coupon":   { "type": "string" },
            "currency": { "type": "string", "enum": ["EUR", "USD", "GBP"] }
          },
          "required": ["orderId", "total", "currency"]
        }
        """;

    private const string Payment = """
        {
          "type": "object",
          "properties": {
            "paymentId": { "type": "string" },
            "orderId":   { "type": "string" },
            "amount":    { "type": "number", "minimum": 0 },
            "currency":  { "type": "string" }
          },
          "required": ["paymentId", "orderId", "amount"]
        }
        """;

    public async Task RunAsync()
    {
        Log.Plain($"Seeding the '{environment}' environment (nothing is overwritten if it is already there)");

        await SubjectAsync(Names.OrderPlaced, [
            (OrderV1, "first version"),
            (OrderV2, "optional coupon"),
            (OrderV3Breaking, "currency becomes required"),
        ]);

        await SubjectAsync(Names.PaymentCaptured, [(Payment, "first version")]);

        // The orders contract ENFORCES: a bad order is refused before it reaches the broker.
        await ContractAsync(
            Names.OrdersContract,
            "ENFORCE",
            Names.OrdersExchange,
            "orders.*",
            Names.BillingQueue,
            Names.OrderPlaced);

        // The payments contract only MONITORS: a bad payment is delivered and counted, which is the mode
        // a team starts in. Switch it to ENFORCE on the Contracts page and watch what changes.
        await ContractAsync(
            Names.PaymentsContract,
            "MONITOR",
            Names.PaymentsExchange,
            "payments.*",
            Names.LedgerQueue,
            Names.PaymentCaptured);

        Log.Plain();
    }

    private async Task SubjectAsync(string name, (string Schema, string Why)[] versions)
    {
        using var existing = await http.GetAsync($"/v1/environments/{environment}/subjects/{name}");

        if (existing.IsSuccessStatusCode)
        {
            Log.Plain($"  · subject {name} is already registered");
            return;
        }

        await Expect(
            existing, HttpStatusCode.NotFound, $"look up subject {name}");

        using var created = await http.PostAsJsonAsync(
            $"/v1/environments/{environment}/subjects",
            new { name, format = "json", owner = Owner });
        await Expect(created, HttpStatusCode.Created, $"create subject {name}");

        foreach (var (schema, why) in versions)
        {
            using var response = await http.PostAsJsonAsync(
                $"/v1/environments/{environment}/subjects/{name}/versions",
                new { schema, registeredBy = Owner, changelog = why });
            await Expect(response, HttpStatusCode.Created, $"register a version of {name}");

            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = body.RootElement;
            var ordinal = root.TryGetProperty("ordinal", out var o) ? o.GetInt32() : 0;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;

            var held = status == "AWAITING_APPROVAL";
            Log.Plain($"  + {name} v{ordinal}: {why}" + (held ? "   <- held at the approval gate" : string.Empty));
        }
    }

    private async Task ContractAsync(
        string name, string enforcement, string exchange, string routingKey, string queue, string subject)
    {
        using var existing = await http.GetAsync($"/v1/environments/{environment}/contracts/{name}");

        if (existing.IsSuccessStatusCode)
        {
            Log.Plain($"  · contract {name} already exists");
            return;
        }

        await Expect(existing, HttpStatusCode.NotFound, $"look up contract {name}");

        var subjects = new[] { new { subject, selector = "latest" } };

        using var created = await http.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts", new { name, enforcement });
        await Expect(created, HttpStatusCode.Created, $"create contract {name}");

        using var publish = await http.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts/{name}/publishes",
            new { exchange, routingKeyPattern = routingKey, subjects });
        await Expect(publish, HttpStatusCode.Created, $"bind {exchange} for publishing");

        using var consume = await http.PostAsJsonAsync(
            $"/v1/environments/{environment}/contracts/{name}/consumes", new { queue, subjects });
        await Expect(consume, HttpStatusCode.Created, $"bind {queue} for consuming");

        Log.Plain($"  + contract {name} [{enforcement}]: {exchange} / {routingKey} and {queue} carry {subject}");
    }

    /// <summary>Says what the registry said, in words, when it was not what the demo needed.</summary>
    private static async Task Expect(HttpResponseMessage response, HttpStatusCode wanted, string doing)
    {
        if (response.StatusCode == wanted)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();

        var hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                " The registry needs a credential: set CONCORDAT_API_KEY to an API key (Settings > API keys) " +
                "that holds subject:write and contract:write. See docs/DEMO.md.",
            HttpStatusCode.Forbidden =>
                " The API key is missing a scope; the demo needs subject:write and contract:write.",
            _ => string.Empty,
        };

        throw new DemoException(
            $"Could not {doing}: the registry answered {(int)response.StatusCode} {response.StatusCode}.{hint}" +
            (string.IsNullOrWhiteSpace(body) ? string.Empty : $"\n  {body}"));
    }
}

/// <summary>A failure the person running the demo can act on, reported without a stack trace.</summary>
internal sealed class DemoException(string message) : Exception(message);
