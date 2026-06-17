using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SRW.Core.Abstractions;
using SRW.Domain.Entities;

namespace SRW.Infrastructure.BackgroundJobs;

/// <summary>
/// Background worker that provisions K8s/Terraform resources for newly launched
/// sessions. Implements ISessionProvisioningQueue so Core can enqueue work
/// without knowing anything about hosting or DI.
///
/// Each enqueued item runs in its own DI scope so repository and orchestrator
/// instances are not shared across concurrent provisioning operations.
/// </summary>
public sealed class SessionLaunchWorker : BackgroundService, ISessionProvisioningQueue
{
    private readonly record struct LaunchItem(
        Guid SessionId,
        Guid WorkspaceId,
        Guid ApplicationId,
        string UserId);

    private readonly Channel<LaunchItem> _channel =
        Channel.CreateUnbounded<LaunchItem>(new UnboundedChannelOptions { SingleReader = true });

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SessionLaunchWorker> _log;

    public SessionLaunchWorker(IServiceScopeFactory scopeFactory, ILogger<SessionLaunchWorker> log)
    {
        _scopeFactory = scopeFactory;
        _log          = log;
    }

    // Called synchronously from SessionLauncher (the HTTP request thread).
    public void EnqueueLaunch(Guid sessionId, Guid workspaceId, Guid applicationId, string userId)
        => _channel.Writer.TryWrite(new LaunchItem(sessionId, workspaceId, applicationId, userId));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            await ProvisionAsync(item, stoppingToken);
        }
    }

    private async Task ProvisionAsync(LaunchItem item, CancellationToken ct)
    {
        _log.LogInformation("SessionLaunchWorker: provisioning session {SessionId}", item.SessionId);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var workspaceRepo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var sessionRepo   = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        var k8s           = scope.ServiceProvider.GetRequiredService<IKubernetesOrchestrator>();

        var session = await sessionRepo.GetByIdAsync(item.SessionId, ct);
        if (session is null)
        {
            _log.LogWarning("SessionLaunchWorker: session {SessionId} not found — skipping", item.SessionId);
            return;
        }

        var workspace = await workspaceRepo.GetByIdAsync(item.WorkspaceId, ct);
        var application = await workspaceRepo.GetApplicationAsync(item.ApplicationId, ct);

        if (workspace is null || application is null)
        {
            _log.LogError("SessionLaunchWorker: workspace or application missing for session {SessionId}", item.SessionId);
            session.Status = SessionStatus.Failed;
            await sessionRepo.UpdateAsync(session, ct);
            return;
        }

        try
        {
            var result = await k8s.LaunchSessionAsync(workspace, application, session, ct);

            session.DeploymentName = result.DeploymentName;
            session.ServiceName    = result.ServiceName;
            session.AccessUrl      = result.AccessUrl;
            session.StartedAtUtc   = DateTime.UtcNow;
            await sessionRepo.UpdateAsync(session, ct);

            _log.LogInformation(
                "SessionLaunchWorker: session {SessionId} provisioned — app={App} user={User} url={Url}",
                item.SessionId, application.Name, item.UserId, result.AccessUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "SessionLaunchWorker: session {SessionId} provisioning failed", item.SessionId);
            session.Status = SessionStatus.Failed;
            await sessionRepo.UpdateAsync(session, ct);
        }
    }
}
