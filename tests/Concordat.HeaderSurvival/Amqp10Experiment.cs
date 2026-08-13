using System.Globalization;
using System.Text;
using Amqp;
using Amqp.Types;
using RabbitMQ.Client;

namespace Concordat.HeaderSurvival;

/// <summary>
/// Does the envelope survive AMQP 1.0 conversion? (ADR-013, M2.5.)
/// </summary>
/// <remarks>
/// <para>
/// <b>The experiment ADR-013 rests on.</b> ADR-013 avoids the <c>x-</c> prefix on the explicit
/// ground that RabbitMQ turns <c>x-</c> headers into AMQP 1.0 <em>message-annotations</em>,
/// which are broker-level metadata an application client is not obliged to surface — while
/// everything else becomes <em>application-properties</em>, which it is. Until this ran, that
/// was an assertion in a document.
/// </para>
/// <para>
/// The check that matters is not merely "the values arrived". It is that they arrived in the
/// <em>right section</em>: an envelope demoted to message-annotations would be invisible to an
/// ordinary AMQP 1.0 consumer even though the bytes were still on the wire.
/// </para>
/// </remarks>
[Collection(BrokerCollection.Name)]
public class Amqp10Experiment(BrokerFixture broker)
{
    [Fact]
    public async Task TheEnvelopeArrivesAsApplicationPropertiesNotMessageAnnotations()
    {
        var queue = $"amqp10-{Guid.NewGuid():N}";

        await using (var connection = await broker.ConnectAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);

            // Published over AMQP 0-9-1, exactly as a Concordat producer would.
            await channel.BasicPublishAsync(
                string.Empty, queue, mandatory: true, Probe.Properties(), Probe.Body);
        }

        var received = await ReceiveOverAmqp10Async(queue);

        Assert.True(received is not null, "nothing arrived over AMQP 1.0.");

        var applicationProperties = received.ApplicationProperties?.Map ?? [];
        var annotations = received.MessageAnnotations?.Map ?? [];

        foreach (var (key, expected) in Probe.Envelope)
        {
            Assert.True(
                applicationProperties.ContainsKey(key),
                $"'{key}' is not an application-property. Present: " +
                $"[{string.Join(", ", applicationProperties.Keys.Select(k => k.ToString()).Order(StringComparer.Ordinal))}]. " +
                $"Annotations: [{string.Join(", ", annotations.Keys.Select(k => k.ToString()).Order(StringComparer.Ordinal))}]. " +
                "ADR-013's avoidance of the x- prefix exists precisely to keep this out of annotations.");

            Assert.Equal(expected, Stringify(applicationProperties[key]));
        }

        // The other half of the claim: nothing of ours was demoted.
        Assert.DoesNotContain(
            annotations.Keys.Select(k => k.ToString() ?? string.Empty),
            k => k.Contains("concordat", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnXPrefixedHeaderIsDemotedToAnAnnotation()
    {
        // The control. Without it, the previous test only shows that our headers arrive — not
        // that the x- prefix is what would have cost us, which is the entire basis of ADR-013.
        var queue = $"amqp10-x-{Guid.NewGuid():N}";

        await using (var connection = await broker.ConnectAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);

            var headers = Probe.AmqpHeaders();
            headers["x-concordat-counterfactual"] = "what the x- prefix would have cost us";

            await channel.BasicPublishAsync(
                string.Empty,
                queue,
                mandatory: true,
                new BasicProperties { Headers = headers, Type = "acme.orders.OrderCreated" },
                Probe.Body);
        }

        var received = await ReceiveOverAmqp10Async(queue);
        Assert.NotNull(received);

        var applicationProperties = received.ApplicationProperties?.Map ?? [];
        var annotations = (received.MessageAnnotations?.Map ?? [])
            .Keys.Select(k => k.ToString() ?? string.Empty).ToList();

        Assert.True(
            annotations.Contains("x-concordat-counterfactual", StringComparer.Ordinal),
            "the x- header was NOT demoted to an annotation, so ADR-013's stated reason for " +
            $"avoiding the prefix does not hold on {BrokerFixture.Image}. Annotations: " +
            $"[{string.Join(", ", annotations.Order(StringComparer.Ordinal))}]");

        Assert.DoesNotContain(
            applicationProperties.Keys.Select(k => k.ToString() ?? string.Empty),
            k => k == "x-concordat-counterfactual");
    }

    [Fact]
    public async Task ModeAAloneDoesNotSurviveAmqp10Conversion()
    {
        // The finding that changes advice rather than confirming it.
        //
        // properties.type does NOT become the AMQP 1.0 `subject`, which is where anyone would
        // look for it. It is demoted to the message-annotation x-basic-type — precisely the
        // fate ADR-013 keeps the envelope out of by avoiding the x- prefix.
        //
        // So for an estate containing AMQP 1.0 consumers the envelope is not an optimisation,
        // it is the only thing that works: a Mode A message whose subject lives solely in
        // properties.type arrives with that subject in a section an ordinary 1.0 client is not
        // obliged to surface.
        var queue = $"amqp10-subject-{Guid.NewGuid():N}";

        await using (var connection = await broker.ConnectAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
            await channel.BasicPublishAsync(
                string.Empty, queue, mandatory: true, Probe.Properties(), Probe.Body);
        }

        var received = await ReceiveOverAmqp10Async(queue);
        Assert.NotNull(received);

        var annotations = (received.MessageAnnotations?.Map ?? [])
            .ToDictionary(e => e.Key.ToString() ?? string.Empty, e => Stringify(e.Value), StringComparer.Ordinal);

        Assert.Null(received.Properties?.Subject);
        Assert.Equal("acme.orders.OrderCreated", annotations["x-basic-type"]);

        // content-type does make it into the standard properties section, so the format hint
        // that Mode B carries in content-type survives where Mode A's subject does not.
        Assert.Equal("application/json", received.Properties?.ContentType);

        // The envelope, meanwhile, is right where a 1.0 consumer will find it. This is the
        // comparison that justifies the guidance: same message, two carriers, one survives.
        var applicationProperties = received.ApplicationProperties?.Map ?? [];
        Assert.Equal(
            "acme.orders.OrderCreated",
            Stringify(applicationProperties["concordat-subject"]));
    }

    private static string Stringify(object? value) => value switch
    {
        null => "<null>",
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        string text => text,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "<null>",
    };

    private async Task<Message?> ReceiveOverAmqp10Async(string queue)
    {
        // RabbitMQ 4.x speaks AMQP 1.0 natively on the same port as 0-9-1, and addresses a
        // queue as /queues/{name} under its v2 address scheme.
        var connection = await Connection.Factory
            .CreateAsync(new Address($"amqp://guest:guest@{broker.Host}:{broker.AmqpPort}"))
            .ConfigureAwait(false);

        try
        {
            var session = new Session(connection);
            var receiver = new ReceiverLink(session, "concordat-probe", $"/queues/{queue}");

            var message = await receiver.ReceiveAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            if (message is not null)
            {
                receiver.Accept(message);
            }

            await receiver.CloseAsync().ConfigureAwait(false);
            await session.CloseAsync().ConfigureAwait(false);
            return message;
        }
        finally
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
    }
}
