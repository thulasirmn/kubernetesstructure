using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SRW.Core.Abstractions;
using SRW.Core.Services;
using SRW.Domain.Entities;
using SRW.Infrastructure.Messaging;

namespace SRW.Infrastructure.BackgroundJobs;

/// <summary>
/// Consumes WorkspaceCleanupMessage from Service Bus.
/// Deletes K8s namespace, Azure Storage Account, and the Cosmos secret document,
/// then marks the workspace as Deleted (or removes it from Cosmos entirely).
/// </summary>
public sealed class WorkspaceCleanupConsumer : BackgroundService
{
    private readonly IServiceScopeFactory   _scopeFactory;
    private readonly ServiceBusOptions      _sbOptions;
    private readonly ILogger<WorkspaceCleanupConsumer> _log;
    private ServiceBusProcessor?            _processor;

    public WorkspaceCleanupConsumer(
        IServiceScopeFactory scopeFactory,
        IOptions<ServiceBusOptions> sbOptions,
        ILogger<WorkspaceCleanupConsumer> log)
    {
        _scopeFactory = scopeFactory;
        _sbOptions    = sbOptions.Value;
        _log          = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var client = ServiceBusClientFactory.Create(_sbOptions);

        _processor = client.CreateProcessor(
            _sbOptions.WorkspaceCleanupQueue,
            new ServiceBusProcessorOptions
            {
                MaxConcurrentCalls            = 3,
                AutoCompleteMessages          = false,
                MaxAutoLockRenewalDuration    = TimeSpan.FromMinutes(15)
            });

        _processor.ProcessMessageAsync += HandleMessageAsync;
        _processor.ProcessErrorAsync   += HandleErrorAsync;

        await _processor.StartProcessingAsync(stoppingToken);

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
        finally
        {
            await _processor.StopProcessingAsync();
            await _processor.DisposeAsync();
            await client.DisposeAsync();
        }
    }

    private async Task HandleMessageAsync(ProcessMessageEventArgs args)
    {
        WorkspaceCleanupMessage? msg;
        try
        {
            msg = JsonSerializer.Deserialize<WorkspaceCleanupMessage>(args.Message.Body.ToString());
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to deserialise WorkspaceCleanupMessage — dead-lettering");
            await args.DeadLetterMessageAsync(args.Message, "DeserializationFailed", ex.Message);
            return;
        }

        if (msg is null)
        {
            await args.DeadLetterMessageAsync(args.Message, "NullMessage", "Deserialized to null");
            return;
        }

        _log.LogInformation("WorkspaceCleanupConsumer: cleaning up workspace {WsId}", msg.WorkspaceId);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<WorkspaceProvisioningService>();

        try
        {
            await svc.CleanupAsync(msg.WorkspaceId, args.CancellationToken);
            await args.CompleteMessageAsync(args.Message);
            _log.LogInformation("Workspace {WsId} cleaned up", msg.WorkspaceId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Workspace {WsId} cleanup failed — abandoning", msg.WorkspaceId);
            await args.AbandonMessageAsync(args.Message);
        }
    }

    private Task HandleErrorAsync(ProcessErrorEventArgs args)
    {
        _log.LogError(args.Exception,
            "Service Bus processor error: source={Source} entity={Entity}",
            args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }
}
