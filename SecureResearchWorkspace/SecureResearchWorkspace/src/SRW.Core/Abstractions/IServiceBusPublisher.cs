namespace SRW.Core.Abstractions;

public interface IServiceBusPublisher
{
    Task PublishAsync<T>(string queueName, T message, CancellationToken ct = default)
        where T : class;
}
