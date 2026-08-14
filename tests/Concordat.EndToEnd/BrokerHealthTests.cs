using Concordat.Application.Abstractions;
using Concordat.Domain.Registry;
using Concordat.Infrastructure;

namespace Concordat.EndToEnd;

/// <summary>
/// M7.1's broker health check, against a real broker.
/// </summary>
/// <remarks>
/// The one part of M7.1 that cannot be proven without a broker: the whole point of completing
/// an AMQP handshake rather than probing the port is that it detects a wrong virtual host,
/// which a socket check cannot.
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class BrokerHealthTests(StackFixture stack)
{
    // No stored credentials in this suite: it tests that a real handshake happens, and the
    // brokers here carry their credentials in the URI. M7.2's storage is proven separately.
    private readonly RabbitMqBrokerHealthProbe _probe = new(new NoStoredCredentials());

    private sealed class NoStoredCredentials : ICredentialStore
    {
        public Task<string> StoreAsync(
            BrokerCredential credential, string? existingRef, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BrokerCredential?> ResolveAsync(
            string credentialRef, CancellationToken cancellationToken) =>
            Task.FromResult<BrokerCredential?>(null);

        public Task RemoveAsync(string credentialRef, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private BrokerConnection Broker(string? virtualHost = null) =>
        BrokerConnection.Create(
            "under-test",
            $"amqp://guest:guest@{stack.BrokerHost}:{stack.BrokerPort}",
            virtualHost).Value;

    [Fact]
    public async Task AReachableBrokerReportsReachable()
    {
        var result = await _probe.ProbeAsync(Broker(), CancellationToken.None);

        Assert.True(result.Reachable, result.Error);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task WrongCredentialsAreDetected()
    {
        // The case that justifies completing a handshake instead of probing the port: the
        // socket accepts and the TCP connection succeeds, and the broker then refuses the
        // login. A port check calls this healthy.
        var broker = BrokerConnection.Create(
            "bad-credentials",
            $"amqp://wrong:wrong@{stack.BrokerHost}:{stack.BrokerPort}").Value;

        var result = await _probe.ProbeAsync(broker, CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ANonexistentVirtualHostIsNotRefusedByThisBroker()
    {
        // Recorded because it is surprising and was assumed otherwise while writing this
        // suite: rabbitmq:4.1 accepts a connection naming a virtual host that does not exist,
        // so the check cannot be relied on to catch that misconfiguration. Verified against a
        // bare RabbitMQ.Client connection as well, so it is the broker's behaviour and not
        // something the probe does. If a future broker version starts refusing, this test
        // fails and the guarantee can be strengthened deliberately rather than by accident.
        var result = await _probe.ProbeAsync(
            Broker("/does-not-exist"), CancellationToken.None);

        Assert.True(result.Reachable, result.Error);
    }

    [Fact]
    public async Task AnUnreachableHostIsDataRatherThanAnException()
    {
        // A registry that threw because a broker was down would have adopted someone else's
        // outage. The caller asked what the state was, and "unreachable" answers it.
        var broker = BrokerConnection.Create("gone", "amqp://127.0.0.1:1").Value;

        var result = await _probe.ProbeAsync(broker, CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task TheOutcomeIsRecordedOnTheConnection()
    {
        var broker = Broker();
        var result = await _probe.ProbeAsync(broker, CancellationToken.None);

        broker.RecordCheck(result.Reachable, DateTimeOffset.UtcNow, result.Error);

        Assert.Equal(BrokerStatus.Reachable, broker.Status);
        Assert.NotNull(broker.LastCheckedAt);
    }
}
