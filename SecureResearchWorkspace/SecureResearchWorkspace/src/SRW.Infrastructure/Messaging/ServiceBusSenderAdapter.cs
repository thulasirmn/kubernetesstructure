using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using SRW.Core.Abstractions;

namespace SRW.Infrastructure.Messaging;

/// <summary>
/// Thin wrapper that serialises messages to JSON and dispatches them via Azure Service Bus.
/// Uses connection string if provided (local dev), otherwise DefaultAzureCredential (production AKS).
/// Senders are cached per queue to amortize AMQP link creation cost.
/// </summary>
public sealed class ServiceBusSenderAdapter : IServiceBusPublisher, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new();

    public ServiceBusSenderAdapter(IOptions<ServiceBusOptions> options)
    {
        _client = ServiceBusClientFactory.Create(options.Value);
    }

    public async Task PublishAsync<T>(string queueName, T message, CancellationToken ct = default)
        where T : class
    {
        var sender = _senders.GetOrAdd(queueName, q => _client.CreateSender(q));
        var body   = JsonSerializer.SerializeToUtf8Bytes(message);
        var sbMsg  = new ServiceBusMessage(body)
        {
            ContentType = "application/json"
        };
        await sender.SendMessageAsync(sbMsg, ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sender in _senders.Values)
            await sender.DisposeAsync();
        await _client.DisposeAsync();
    }
}
