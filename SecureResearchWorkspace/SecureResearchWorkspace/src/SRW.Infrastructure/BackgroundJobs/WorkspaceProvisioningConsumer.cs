using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SRW.Core.Abstractions;
using SRW.Core.Services;
using SRW.Infrastructure.Messaging;

namespace SRW.Infrastructure.BackgroundJobs;

/// <summary>
/// Consumes WorkspaceProvisionMessage from Service Bus and executes the full
/// Azure Storage + Kubernetes provisioning saga.
/// MaxConcurrentCalls = 5 so up to 5 workspaces provision in parallel.
/// MaxAutoLockRenewalDuration = 10 min — ARM LROs can take 60-90 s each.
/// </summary>
public sealed class WorkspaceProvisioningConsumer : BackgroundService
{
    private readonly IServiceScopeFactory       _scopeFactory;
    private readonly ServiceBusOptions          _sbOptions;
    private readonly BackgroundJobOptions       _jobOptions;
    private readonly ILogger<WorkspaceProvisioningConsumer> _log;
    private ServiceBusProcessor?                _processor;

    public WorkspaceProvisioningConsumer(
        IServiceScopeFactory       scopeFactory,
        IOptions<ServiceBusOptions>    sbOptions,
        IOptions<BackgroundJobOptions> jobOptions,
        ILogger<WorkspaceProvisioningConsumer> log)
    {
        _scopeFactory = scopeFactory;
        _sbOptions    = sbOptions.Value;
        _jobOptions   = jobOptions.Value;
        _log          = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var client = ServiceBusClientFactory.Create(_sbOptions);

        _processor = client.CreateProcessor(
            _sbOptions.WorkspaceProvisionQueue,
            new ServiceBusProcessorOptions
            {
                MaxConcurrentCalls            = _jobOptions.ProvisioningMaxConcurrentCalls,
                AutoCompleteMessages          = false,
                MaxAutoLockRenewalDuration    = TimeSpan.FromMinutes(_jobOptions.ProvisioningMaxAutoLockRenewMinutes)
            });

        _processor.ProcessMessageAsync += HandleMessageAsync;
        _processor.ProcessErrorAsync   += HandleErrorAsync;

        await _processor.StartProcessingAsync(stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
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
        WorkspaceProvisionMessage? msg;
        try
        {
            msg = JsonSerializer.Deserialize<WorkspaceProvisionMessage>(args.Message.Body.ToString());
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to deserialise WorkspaceProvisionMessage — dead-lettering");
            await args.DeadLetterMessageAsync(args.Message, "DeserializationFailed", ex.Message);
            return;
        }

        if (msg is null)
        {
            await args.DeadLetterMessageAsync(args.Message, "NullMessage", "Deserialized to null");
            return;
        }

        _log.LogInformation("WorkspaceProvisioningConsumer: provisioning workspace {WsId}", msg.WorkspaceId);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<WorkspaceProvisioningService>();

        try
        {
            await svc.ProvisionAsync(msg.WorkspaceId, args.CancellationToken);
            await args.CompleteMessageAsync(args.Message);
            _log.LogInformation("Workspace {WsId} provisioned successfully", msg.WorkspaceId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Workspace {WsId} provisioning failed — abandoning for retry", msg.WorkspaceId);
            // Abandon returns the message to the queue; the dead-letter limit on the queue
            // configuration (default 10 deliveries) prevents infinite loops.
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
