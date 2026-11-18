using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace PaymentLab.ServiceDefaults;

public sealed class RabbitMqOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string User { get; set; } = "lab";
    public string Password { get; set; } = "lab";
    public string VirtualHost { get; set; } = "/";
    public ushort Prefetch { get; set; } = 50;

    public static RabbitMqOptions From(IConfiguration cfg)
    {
        var o = new RabbitMqOptions();
        cfg.GetSection("RabbitMq").Bind(o);
        return o;
    }
}

/// <summary>
/// One long-lived AMQP connection per process; channels are created per publisher/consumer.
/// RabbitMQ.Client recovers the connection, channels and consumers automatically after a network failure.
/// </summary>
public sealed class RabbitMqConnection(RabbitMqOptions options, ILogger<RabbitMqConnection> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;

    public RabbitMqOptions Options => options;

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        if (_connection is not null) return _connection;
        await _lock.WaitAsync(ct);
        try
        {
            if (_connection is not null) return _connection;
            var factory = new ConnectionFactory
            {
                HostName = options.Host,
                Port = options.Port,
                UserName = options.User,
                Password = options.Password,
                VirtualHost = options.VirtualHost,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(3),
                RequestedHeartbeat = TimeSpan.FromSeconds(30),
                ClientProvidedName = AppDomain.CurrentDomain.FriendlyName,
            };
            var connection = await factory.CreateConnectionAsync(ct);
            connection.ConnectionShutdownAsync += (_, e) =>
            {
                logger.LogWarning("RabbitMQ connection shut down: {Reason}", e.ReplyText);
                return Task.CompletedTask;
            };
            connection.RecoverySucceededAsync += (_, _) =>
            {
                logger.LogInformation("RabbitMQ connection recovered");
                return Task.CompletedTask;
            };
            logger.LogInformation("Connected to RabbitMQ {Host}:{Port}", options.Host, options.Port);
            _connection = connection;
            return connection;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            try { await _connection.CloseAsync(); } catch { /* shutting down */ }
            _connection.Dispose();
        }
    }
}
