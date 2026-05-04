using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SRW.Core.Abstractions;
using SRW.Infrastructure.Messaging;

namespace SRW.Infrastructure.BackgroundJobs;

/// <summary>
/// Periodically scans for sessions that have not had any activity for longer than
/// IdleSessionThresholdHours and publishes a SessionStopMessage for each one.
/// The actual K8s teardown is handled by SessionStopConsumer.
/// </summary>
public sealed class IdleSessionReaper : BackgroundService
{
    private readonly IServiceScopeFactory   _scopeFactory;
    private readonly BackgroundJobOptions   _jobOptions;
    private readonly ServiceBusOptions      _sbOptions;
    private readonly ILogger<IdleSessionReaper> _log;

    public IdleSessionReaper(
        IServiceScopeFactory scopeFactory,
        IOptions<BackgroundJobOptions> jobOptions,
        IOptions<ServiceBusOptions>    sbOptions,
        ILogger<IdleSessionReaper> log)
    {
        _scopeFactory = scopeFactory;
        _jobOptions   = jobOptions.Value;
        _sbOptions    = sbOptions.Value;
        _log          = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_jobOptions.IdleReaperIntervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await ReapAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "IdleSessionReaper tick failed");
            }
        }
    }

    private async Task ReapAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sessionRepo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        var publisher   = scope.ServiceProvider.GetRequiredService<IServiceBusPublisher>();

        var threshold   = TimeSpan.FromHours(_jobOptions.IdleSessionThresholdHours);
        var idleSessions = await sessionRepo.ListIdleAsync(threshold, ct);

        if (idleSessions.Count == 0) return;

        _log.LogInformation("IdleSessionReaper: found {Count} idle sessions to reap", idleSessions.Count);

        foreach (var session in idleSessions)
        {
            try
            {
                await publisher.PublishAsync(
                    _sbOptions.SessionStopQueue,
                    new SessionStopMessage(session.Id, session.WorkspaceId),
                    ct);
                _log.LogInformation("Queued stop for idle session {Id} (workspace {WsId})", session.Id, session.WorkspaceId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Failed to enqueue stop for idle session {Id}", session.Id);
            }
        }
    }
}
