using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SRW.Core.Abstractions;
using SRW.Domain.Entities;
using SRW.Infrastructure.Messaging;

namespace SRW.Infrastructure.BackgroundJobs;

/// <summary>
/// Consumes SessionStopMessage from Service Bus, tears down the Kubernetes
/// Deployment/Service/Ingress, and marks the session Stopped in Cosmos DB.
/// </summary>
public sealed class SessionStopConsumer : BackgroundService
{
    private readonly IServiceScopeFactory   _scopeFactory;
    private readonly ServiceBusOptions      _sbOptions;
    private readonly ILogger<SessionStopConsumer> _log;
    private ServiceBusProcessor?            _processor;

    public SessionStopConsumer(
        IServiceScopeFactory scopeFactory,
        IOptions<ServiceBusOptions> sbOptions,
        ILogger<SessionStopConsumer> log)
    {
        _scopeFactory = scopeFactory;
        _sbOptions    = sbOptions.Value;
        _log          = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var client = ServiceBusClientFactory.Create(_sbOptions);

        _processor = client.CreateProcessor(
            _sbOptions.SessionStopQueue,
            new ServiceBusProcessorOptions
            {
                MaxConcurrentCalls   = 10,
                AutoCompleteMessages = false
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
        SessionStopMessage? msg;
        try
        {
            msg = JsonSerializer.Deserialize<SessionStopMessage>(args.Message.Body.ToString());
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to deserialise SessionStopMessage — dead-lettering");
            await args.DeadLetterMessageAsync(args.Message, "DeserializationFailed", ex.Message);
            return;
        }

        if (msg is null)
        {
            await args.DeadLetterMessageAsync(args.Message, "NullMessage", "Deserialized to null");
            return;
        }

        _log.LogInformation("SessionStopConsumer: stopping session {SessionId}", msg.SessionId);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sessionRepo   = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        var workspaceRepo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var k8s           = scope.ServiceProvider.GetRequiredService<IKubernetesOrchestrator>();

        try
        {
            var session = await sessionRepo.GetByIdAsync(msg.SessionId, args.CancellationToken);
            if (session is null)
            {
                _log.LogWarning("Session {Id} not found — completing message", msg.SessionId);
                await args.CompleteMessageAsync(args.Message);
                return;
            }

            if (session.Status == SessionStatus.Stopped)
            {
                await args.CompleteMessageAsync(args.Message);
                return;
            }

            var ws = await workspaceRepo.GetByIdAsync(msg.WorkspaceId, args.CancellationToken);
            if (ws is not null)
                await k8s.StopSessionAsync(ws.K8sNamespace, session.DeploymentName, session.ServiceName, session.Id, args.CancellationToken);

            session.Status       = SessionStatus.Stopped;
            session.StoppedAtUtc = DateTime.UtcNow;
            await sessionRepo.UpdateAsync(session, args.CancellationToken);

            await args.CompleteMessageAsync(args.Message);
            _log.LogInformation("Session {Id} stopped", msg.SessionId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to stop session {Id} — abandoning", msg.SessionId);
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
