using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SRW.Core.Abstractions;
using SRW.Domain.Entities;

namespace SRW.Infrastructure.BackgroundJobs;

/// <summary>
/// Polls Kubernetes every N seconds for sessions that are in Starting or Running state
/// and syncs the status back to Cosmos DB. This avoids polling K8s on every API read.
/// </summary>
public sealed class SessionStatusPoller : BackgroundService
{
    private readonly IServiceScopeFactory    _scopeFactory;
    private readonly BackgroundJobOptions    _options;
    private readonly ILogger<SessionStatusPoller> _log;

    public SessionStatusPoller(
        IServiceScopeFactory scopeFactory,
        IOptions<BackgroundJobOptions> options,
        ILogger<SessionStatusPoller> log)
    {
        _scopeFactory = scopeFactory;
        _options      = options.Value;
        _log          = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.SessionStatusPollSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await PollAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "SessionStatusPoller tick failed");
            }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sessionRepo   = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        var workspaceRepo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var k8s           = scope.ServiceProvider.GetRequiredService<IKubernetesOrchestrator>();

        var activeSessions = await sessionRepo.ListByStatusAsync(SessionStatus.Starting, ct);
        activeSessions.AddRange(await sessionRepo.ListByStatusAsync(SessionStatus.Running, ct));

        if (activeSessions.Count == 0) return;

        _log.LogDebug("SessionStatusPoller: checking {Count} active sessions", activeSessions.Count);

        foreach (var session in activeSessions)
        {
            try
            {
                var ws = await workspaceRepo.GetByIdAsync(session.WorkspaceId, ct);
                if (ws is null) continue;

                var k8sStatus = await k8s.GetSessionStatusAsync(ws.K8sNamespace, session.DeploymentName, ct);

                if (k8sStatus == session.Status) continue;

                session.Status = k8sStatus;

                if (k8sStatus == SessionStatus.Running && session.StartedAtUtc is null)
                    session.StartedAtUtc = DateTime.UtcNow;

                await sessionRepo.UpdateAsync(session, ct);
                _log.LogInformation("Session {Id} status synced: {Status}", session.Id, k8sStatus);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Failed to poll status for session {Id}", session.Id);
            }
        }
    }
}
