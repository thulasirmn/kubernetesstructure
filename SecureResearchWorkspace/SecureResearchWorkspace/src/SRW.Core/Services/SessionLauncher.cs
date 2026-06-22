using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SRW.Core.Abstractions;
using SRW.Domain.Entities;

namespace SRW.Core.Services;

/// <summary>
/// Handles session launch. LaunchAsync is synchronous-only:
/// it validates inputs, persists a Starting session record, and delegates the
/// actual Terraform provisioning to ISessionProvisioningQueue so the HTTP
/// response returns immediately (202 Accepted). Callers poll
/// GET /sessions/{id} to observe status transitions via SessionStatusPoller.
/// Session stop is handled by SessionStopConsumer (via Service Bus).
/// </summary>
public sealed class SessionLauncher
{
    private readonly IWorkspaceRepository    _workspaceRepo;
    private readonly ISessionRepository      _sessionRepo;
    private readonly IApplicationRepository  _appRepo;
    private readonly ISessionProvisioningQueue _provisioningQueue;
    private readonly ILogger<SessionLauncher> _log;

    public SessionLauncher(
        IWorkspaceRepository workspaceRepo,
        ISessionRepository sessionRepo,
        IApplicationRepository appRepo,
        ISessionProvisioningQueue provisioningQueue,
        ILogger<SessionLauncher> log)
    {
        _workspaceRepo     = workspaceRepo;
        _sessionRepo       = sessionRepo;
        _appRepo           = appRepo;
        _provisioningQueue = provisioningQueue;
        _log               = log;
    }

    /// <summary>
    /// Validates, creates a Starting session in the DB, enqueues provisioning,
    /// and returns immediately. Terraform runs in the background.
    /// </summary>
    public async Task<UserSession> LaunchAsync(
        Guid workspaceId,
        Guid applicationId,
        string userId,
        CancellationToken ct = default)
    {
        var workspace = await _workspaceRepo.GetByIdAsync(workspaceId, ct)
            ?? throw new InvalidOperationException($"Workspace {workspaceId} not found");

        if (workspace.Status != WorkspaceStatus.Active)
            throw new InvalidOperationException($"Workspace is not Active (status={workspace.Status})");

        if (!workspace.ApplicationIds.Contains(applicationId))
            throw new InvalidOperationException(
                $"Application {applicationId} is not assigned to workspace {workspaceId}");

        var application = await _appRepo.GetByIdAsync(applicationId, ct)
            ?? throw new InvalidOperationException($"Application {applicationId} not found in catalog");

        if (!application.Enabled)
            throw new InvalidOperationException($"Application {applicationId} has been disabled");

        // Idempotent — return existing session if already in-flight or running.
        var existing = await _sessionRepo.GetActiveAsync(workspaceId, applicationId, userId, ct);
        if (existing is not null && existing.Status is SessionStatus.Running or SessionStatus.Starting)
        {
            _log.LogInformation("Reusing active session {SessionId} for user {UserId}", existing.Id, userId);
            return existing;
        }

        var session = UserSession.Create(workspaceId, applicationId, userId);
        session.Status = SessionStatus.Starting;
        await _sessionRepo.AddAsync(session, ct);

        _provisioningQueue.EnqueueLaunch(session.Id, workspaceId, applicationId, userId);

        _log.LogInformation(
            "Session {SessionId} queued for provisioning: app={App} user={User}",
            session.Id, application.Name, userId);

        return session;
    }
}
