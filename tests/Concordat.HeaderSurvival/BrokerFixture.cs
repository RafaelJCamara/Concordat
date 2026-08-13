using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using RabbitMQ.Client;

namespace Concordat.HeaderSurvival;

/// <summary>
/// A real RabbitMQ broker with every protocol adapter M2.5 needs to interrogate.
/// </summary>
/// <remarks>
/// <para>
/// The image is pinned. A broker version that changes underneath these experiments produces
/// documentation that was true once and findings nobody can reproduce — and the whole point of
/// M2.5 is to replace assertions about header survival with measurements.
/// </para>
/// <para>
/// Plugins are enabled by mounting <c>enabled_plugins</c> rather than by
/// <c>rabbitmq-plugins enable</c> after start, so the broker comes up once with everything on
/// instead of restarting adapters mid-fixture.
/// </para>
/// </remarks>
public sealed class BrokerFixture : IAsyncLifetime
{
    /// <summary>The broker version these findings were measured against.</summary>
    public const string Image = "rabbitmq:4.1-management";

    private const string EnabledPlugins =
        "[rabbitmq_management,rabbitmq_shovel,rabbitmq_shovel_management," +
        "rabbitmq_federation,rabbitmq_federation_management," +
        "rabbitmq_stomp,rabbitmq_mqtt].";

    private IContainer _container = null!;

    /// <summary>The AMQP 0-9-1 and AMQP 1.0 port on the host.</summary>
    public int AmqpPort => _container.GetMappedPublicPort(5672);

    /// <summary>The STOMP port on the host.</summary>
    public int StompPort => _container.GetMappedPublicPort(61613);

    /// <summary>The MQTT port on the host.</summary>
    public int MqttPort => _container.GetMappedPublicPort(1883);

    /// <summary>The management API port on the host.</summary>
    public int ManagementPort => _container.GetMappedPublicPort(15672);

    /// <summary>The container host.</summary>
    public string Host => _container.Hostname;

    /// <summary>Starts the broker.</summary>
    public async Task InitializeAsync()
    {
        _container = Build();
        await _container.StartAsync().ConfigureAwait(false);
    }

    /// <summary>Stops the broker.</summary>
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Builds a broker container. Public so the federation experiment can raise a second.</summary>
    /// <returns>An unstarted container.</returns>
    public static IContainer Build() => Builder().Build();

    /// <summary>The broker builder, before <c>Build()</c>, so a caller can add a network.</summary>
    /// <returns>A configured builder.</returns>
    public static ContainerBuilder Builder() =>
        new ContainerBuilder(Image)
            .WithPortBinding(5672, assignRandomHostPort: true)
            .WithPortBinding(15672, assignRandomHostPort: true)
            .WithPortBinding(61613, assignRandomHostPort: true)
            .WithPortBinding(1883, assignRandomHostPort: true)
            .WithResourceMapping(
                Encoding.UTF8.GetBytes(EnabledPlugins), "/etc/rabbitmq/enabled_plugins")
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilMessageIsLogged("Server startup complete"));

    /// <summary>Runs a command in any container, for multi-broker setups.</summary>
    /// <param name="container">The target.</param>
    /// <param name="command">Argument vector.</param>
    /// <returns>Standard output, with a failure surfaced as an exception.</returns>
    public static async Task<string> ExecAsync(IContainer container, params string[] command)
    {
        ArgumentNullException.ThrowIfNull(container);

        var result = await container.ExecAsync(command).ConfigureAwait(false);

        return result.ExitCode == 0
            ? result.Stdout
            : throw new InvalidOperationException(
                $"{string.Join(' ', command)} exited {result.ExitCode}: {result.Stderr}{result.Stdout}");
    }

    /// <summary>Opens an AMQP 0-9-1 connection.</summary>
    /// <returns>A connection the caller disposes.</returns>
    public Task<IConnection> ConnectAsync() => ConnectAsync(Host, AmqpPort);

    /// <summary>Opens an AMQP 0-9-1 connection to any broker.</summary>
    /// <param name="host">The host.</param>
    /// <param name="port">The port.</param>
    /// <returns>A connection the caller disposes.</returns>
    public static Task<IConnection> ConnectAsync(string host, int port) =>
        new ConnectionFactory
        {
            HostName = host,
            Port = port,
            UserName = "guest",
            Password = "guest",
        }.CreateConnectionAsync();

    /// <summary>Runs a command inside the container, for the shovel and federation setup.</summary>
    /// <param name="command">Argument vector.</param>
    /// <returns>Standard output, with a failure surfaced as an exception.</returns>
    public Task<string> ExecAsync(params string[] command) => ExecAsync(_container, command);
}

/// <summary>Marks a class as sharing one broker.</summary>
/// <remarks>
/// Named for xunit's collection concept, not the BCL's — matching
/// <c>PostgresCollection</c>.
/// </remarks>
[CollectionDefinition(Name)]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "'Collection' is xunit's own term for a shared-fixture group.")]
public sealed class BrokerCollection : ICollectionFixture<BrokerFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "broker";
}
