using System.Text;
using System.Text.Json;
using Concordat.Client;
using Concordat.Domain.Registry;
using Concordat.Formats.Json;
using Concordat.RabbitMq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Demo;

/// <summary>Where the demo finds its registry and its broker. Every value has a default for the compose stack.</summary>
internal sealed record Settings(
    Uri Registry,
    string? ApiKey,
    string Environment,
    string BrokerHost,
    int BrokerPort,
    string BrokerUser,
    string BrokerPassword)
{
    public static Settings FromEnvironment()
    {
        static string? Get(string name) =>
            System.Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

        return new Settings(
            new Uri(Get("CONCORDAT_URL") ?? "http://localhost:5062"),
            Get("CONCORDAT_API_KEY"),
            Get("CONCORDAT_ENVIRONMENT") ?? "dev",
            Get("RABBITMQ_HOST") ?? "localhost",
            int.TryParse(Get("RABBITMQ_PORT"), out var port) ? port : 5672,
            Get("RABBITMQ_USER") ?? "guest",
            Get("RABBITMQ_PASSWORD") ?? "guest");
    }

    public HttpClient RegistryHttp()
    {
        var http = new HttpClient { BaseAddress = Registry };

        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            http.DefaultRequestHeaders.Authorization = new("Bearer", ApiKey);
        }

        return http;
    }
}

/// <summary>
/// One service of the imaginary shop, wired the way a real one is: a registry client, an enforcing
/// channel on top of an ordinary RabbitMQ channel, and a timer that tells the registry what went wrong.
/// </summary>
/// <remarks>
/// Each service has its <b>own</b> client and its own name. That is what makes the registry's Services
/// list, its impact analysis and its violation reports able to say <i>who</i>: a violation reported by
/// "gateway" is something a person can go and talk to.
/// </remarks>
internal sealed class DemoService : IAsyncDisposable
{
    private readonly IChannel _raw;
    private readonly CancellationTokenSource _stop = new();
    private Task? _reporting;

    private DemoService(
        string name, ConcordatClient client, IChannel raw, ConcordatChannel channel, LoggingObserver observer)
    {
        Name = name;
        Client = client;
        _raw = raw;
        Channel = channel;
        Observer = observer;
    }

    public string Name { get; }

    public ConcordatClient Client { get; }

    public ConcordatChannel Channel { get; }

    public LoggingObserver Observer { get; }

    /// <summary>The unwrapped channel, for building a consumer on and for topology.</summary>
    public IChannel Raw => _raw;

    public static async Task<DemoService> StartAsync(
        string name,
        Settings settings,
        IConnection connection,
        string[] produces,
        string[] consumes,
        PublishRoute[] publishRoutes,
        string[] consumeQueues)
    {
        var options = new ConcordatClientOptions
        {
            BaseAddress = settings.Registry,
            Environment = settings.Environment,
            ApiKey = settings.ApiKey,

            // Short, so that something you do on the Contracts or Approvals page shows up here within seconds.
            // The defaults (30 s for the latest version, a minute for contracts) are what a service should run.
            LatestTtl = TimeSpan.FromSeconds(5),
            ContractTtl = TimeSpan.FromSeconds(5),

            // Reported at warm-up, which is what fills the registry's Services list and lets impact analysis
            // name a service instead of saying "somebody".
            ServiceName = name,

            // A demo against a registry that is down should say so, not carry on unenforced.
            RequireWarmUp = true,
        };

        foreach (var subject in produces)
        {
            options.Produces.Add(subject);
        }

        // Pinned to a floor, not "latest": a consumer declared as latest can only be reported as "follows the
        // pointer", and impact analysis can then tell you nothing about it.
        foreach (var subject in consumes)
        {
            options.Consumes.Add($"{subject}@>=1");
        }

        foreach (var route in publishRoutes)
        {
            options.PublishRoutes.Add(route);
        }

        foreach (var queue in consumeQueues)
        {
            options.ConsumeQueues.Add(queue);
        }

        // A plain HttpClient: the client adds the Authorization header itself from ApiKey, and a second one on
        // an HttpClient that already carries it is sent twice and refused.
        var client = new ConcordatClient(new HttpClient { BaseAddress = settings.Registry }, options);
        await client.WarmUpAsync();

        var observer = new LoggingObserver(name, client);

        // Monitor is only the fallback for a route no contract covers. Where a contract governs the route,
        // its enforcement decides: that is the point of putting the mode in the registry.
        var rabbit = new ConcordatRabbitMqOptions { Mode = EnforcementMode.Monitor, Observer = observer };

        var enforcer = new SchemaEnforcer(client, rabbit, [new NJsonSchemaPayloadValidator()]);
        var raw = await connection.CreateChannelAsync();
        var channel = new ConcordatChannel(raw, enforcer, rabbit);

        var service = new DemoService(name, client, raw, channel, observer);
        service._reporting = service.ReportViolationsAsync();
        return service;
    }

    /// <summary>
    /// Tells the registry about violations every few seconds. The SDK counts on the delivery path and
    /// reports in bulk elsewhere (decision 25); <i>something has to call the flush</i>, and nothing does it
    /// for you, so a host that wants its violations on the dashboard runs a timer like this one.
    /// </summary>
    private async Task ReportViolationsAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));

        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                await Client.FlushViolationsAsync(_stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>Publishes one message and says what Concordat did about it.</summary>
    public async Task PublishAsync(string exchange, string routingKey, string subject, string label, object payload)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);

        // The subject is the message type (ADR-011): publisher and consumer name different routes, and only the
        // type is known to both.
        var properties = new BasicProperties { Type = subject, ContentType = "application/json" };

        try
        {
            await Channel.BasicPublishAsync(exchange, routingKey, mandatory: false, properties, body);
        }
        catch (ConcordatViolationException ex)
        {
            // The exception names the exchange and key; the log line already says which route this is.
            const string Marker = "was refused: ";
            var at = ex.Message.IndexOf(Marker, StringComparison.Ordinal);
            var why = at >= 0 ? ex.Message[(at + Marker.Length)..] : ex.Message;
            Log.Bad(Name, "✗", $"{label} REFUSED before it reached the broker: {Short(why)}");
            return;
        }

        switch (Observer.LastPublish)
        {
            case { Outcome: EnforcementOutcome.Valid }:
                Log.Ok(Name, "→", $"{label} published");
                break;
            case { Outcome: EnforcementOutcome.Observed } seen:
                Log.Warn(
                    Name,
                    "!",
                    $"{label} breaks its contract but the mode is MONITOR, so it was published anyway: {Short(seen.Detail)}");
                break;
            default:
                Log.Warn(Name, "?", $"{label} published unchecked: {Observer.LastPublish?.Detail}");
                break;
        }
    }

    /// <summary>Starts consuming a queue through the enforcing channel.</summary>
    public async Task ConsumeAsync(string queue, Func<BasicDeliverEventArgs, string> describe)
    {
        var consumer = new AsyncEventingBasicConsumer(_raw);

        consumer.ReceivedAsync += async (_, delivery) =>
        {
            var text = describe(delivery);
            var schema = Header(delivery.BasicProperties.Headers, EnvelopeHeaders.SchemaId);
            var version = Header(delivery.BasicProperties.Headers, EnvelopeHeaders.VersionOrdinal);

            var tail = schema is null ? "(no envelope)" : $"schema {schema[..8]} (v{version})";

            if (Observer.LastConsume is { Outcome: EnforcementOutcome.Observed } seen)
            {
                Log.Warn(Name, "!", $"{text} received, but it breaks the contract on {queue}: {Short(seen.Detail)}");
            }
            else
            {
                Log.Info(Name, "←", $"{text} received  {tail}");
            }

            await _raw.BasicAckAsync(delivery.DeliveryTag, multiple: false);
        };

        // Through the ConcordatChannel, which wraps the consumer. Consuming on the raw channel would validate
        // nothing, which is the bypass the decorator exists to close.
        await Channel.BasicConsumeAsync(queue, autoAck: false, consumer);
    }

    public async Task StopAsync()
    {
        await _stop.CancelAsync();

        if (_reporting is not null)
        {
            await _reporting;
        }

        // One last report, so the violations of the final seconds are not lost to a Ctrl+C.
        await Client.FlushViolationsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Channel.CloseAsync();
        Client.Dispose();
        _stop.Dispose();
    }

    private static string? Header(IDictionary<string, object?>? headers, string name) =>
        headers is not null && headers.TryGetValue(name, out var value) && value is byte[] bytes
            ? Encoding.UTF8.GetString(bytes)
            : null;

    private static string Short(string text) => text.Length <= 140 ? text : text[..137] + "...";
}

/// <summary>
/// Remembers the last decision on each side and reports violations to the registry.
/// </summary>
/// <remarks>
/// This is the one place the SDK is told about <i>outcomes</i>: <see cref="ViolationReportingObserver"/>
/// is a decorator, so whatever a host already counts or logs keeps working and the registry is told about
/// violations in addition. The demo's own observer keeps the tally for the closing summary.
/// </remarks>
internal sealed class LoggingObserver : IEnforcementObserver
{
    private readonly ViolationReportingObserver _reporting;

    public LoggingObserver(string service, IConcordatClient client)
    {
        Service = service;
        Counters = new EnforcementCounters();
        _reporting = new ViolationReportingObserver(Counters, client);
    }

    public string Service { get; }

    public EnforcementCounters Counters { get; }

    public EnforcementEvent? LastPublish { get; private set; }

    public EnforcementEvent? LastConsume { get; private set; }

    public void Observe(EnforcementEvent decision)
    {
        // Set before the publish or the delivery returns to the caller, and each service handles one message at
        // a time, so "the last one" is "this one".
        if (decision.Side is EnforcementSide.Publish)
        {
            LastPublish = decision;
        }
        else
        {
            LastConsume = decision;
        }

        if (decision.Outcome is EnforcementOutcome.Quarantined or EnforcementOutcome.QuarantineFailed)
        {
            Log.Bad(Service, "✗", $"a delivery was quarantined, not handed to the application: {decision.Detail}");
        }

        _reporting.Observe(decision);
    }
}
