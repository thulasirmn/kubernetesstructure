using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SRW.Core.Abstractions;
using SRW.Domain.Entities;

namespace SRW.Infrastructure.BackgroundJobs;

/// <summary>
/// Detects real user activity by sampling pod network rx bytes via the K8s exec API.
/// Runs on a separate, slower timer than SessionStatusPoller so exec overhead stays low.
///
/// Strategy: cumulative bytes received on eth0 inside the session pod only increase when
/// a user sends HTTP traffic through the nginx ingress. If bytes are unchanged for
/// IdleSessionThresholdHours the IdleSessionReaper will stop the session.
///
/// Pod restarts (rx bytes reset to 0) are detected by comparing against the cached
/// previous value — treated conservatively as "active" to avoid a false reap.
/// </summary>
public sealed class SessionActivityWatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundJobOptions _options;
    private readonly ILogger<SessionActivityWatcher> _log;

    // Keyed by session ID. Reset on API restart; first observation always records activity
    // (conservative — don't reap sessions just because we lost the in-memory baseline).
    private readonly Dictionary<Guid, long> _previousRxBytes = new();

    public SessionActivityWatcher(
        IServiceScopeFactory scopeFactory,
        IOptions<BackgroundJobOptions> options,
        ILogger<SessionActivityWatcher> log)
    {
        _scopeFactory = scopeFactory;
        _options      = options.Value;
        _log          = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.ActivityCheckIntervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await CheckAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "SessionActivityWatcher tick failed");
            }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sessionRepo   = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        var workspaceRepo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var k8s           = scope.ServiceProvider.GetRequiredService<IKubernetesOrchestrator>();

        var runningSessions = await sessionRepo.ListByStatusAsync(SessionStatus.Running, ct);
        if (runningSessions.Count == 0) return;

        _log.LogDebug("SessionActivityWatcher: sampling {Count} running sessions", runningSessions.Count);

        foreach (var session in runningSessions)
        {
            try
            {
                var ws = await workspaceRepo.GetByIdAsync(session.WorkspaceId, ct);
                if (ws is null) continue;

                var rxBytes = await k8s.GetPodRxBytesAsync(ws.K8sNamespace, session.DeploymentName, ct);
                if (rxBytes is null) continue;

                var hasPrev = _previousRxBytes.TryGetValue(session.Id, out var prev);

                if (!hasPrev)
                {
                    // First sample after API start — record as active (conservative baseline reset).
                    _previousRxBytes[session.Id] = rxBytes.Value;
                    await sessionRepo.TouchAsync(session.Id, session.WorkspaceId, ct);
                    continue;
                }

                if (rxBytes.Value < prev)
                {
                    // Bytes decreased → pod restarted. New baseline; treat as active.
                    _previousRxBytes[session.Id] = rxBytes.Value;
                    await sessionRepo.TouchAsync(session.Id, session.WorkspaceId, ct);
                    _log.LogInformation("Session {Id}: pod restarted, activity baseline reset", session.Id);
                    continue;
                }

                if (rxBytes.Value > prev)
                {
                    // New bytes received since last sample → user traffic arrived.
                    _previousRxBytes[session.Id] = rxBytes.Value;
                    await sessionRepo.TouchAsync(session.Id, session.WorkspaceId, ct);
                    _log.LogDebug("Session {Id}: active (+{Delta} rx bytes)", session.Id, rxBytes.Value - prev);
                }
                // else rxBytes == prev → no traffic → LastActivityUtc unchanged → reaper clock ticks
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Activity sample failed for session {Id}", session.Id);
            }
        }
    }
}
