using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SRW.Core.Abstractions;
using SRW.Domain.Entities;

namespace SRW.Core.Services;

/// <summary>
/// Launches a per-user, per-application Kubernetes session (deployment + service + ingress).
/// Multiple users in the same workspace can call this concurrently — each gets their own pod
/// but they share the workspace File Share (mounted via CSI with subPath=userId).
/// </summary>
public sealed class SessionLauncher
{
    private readonly IWorkspaceRepository _workspaceRepo;
    private readonly ISessionRepository _sessionRepo;
    private readonly IAzureStorageProvisioner _storage;
    private readonly IWorkspaceSecretStore _secrets;
    private readonly IKubernetesOrchestrator _k8s;
    private readonly ILogger<SessionLauncher> _log;

    public SessionLauncher(
        IWorkspaceRepository workspaceRepo,
        ISessionRepository sessionRepo,
        IAzureStorageProvisioner storage,
        IWorkspaceSecretStore secrets,
        IKubernetesOrchestrator k8s,
        ILogger<SessionLauncher> log)
    {
        _workspaceRepo = workspaceRepo;
        _sessionRepo = sessionRepo;
        _storage = storage;
        _secrets = secrets;
        _k8s = k8s;
        _log = log;
    }

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

        var application = await _workspaceRepo.GetApplicationAsync(applicationId, ct)
            ?? throw new InvalidOperationException($"Application {applicationId} not found");

        if (application.WorkspaceId != workspace.Id)
            throw new InvalidOperationException("Application does not belong to this workspace");

        // If the user already has a running session for this app, return it (idempotent launch).
        var existing = await _sessionRepo.GetActiveAsync(workspaceId, applicationId, userId, ct);
        if (existing is not null && existing.Status is SessionStatus.Running or SessionStatus.Starting)
        {
            _log.LogInformation("Reusing active session {SessionId} for user {UserId}", existing.Id, userId);
            return existing;
        }

        // Ensure user has a folder in the workspace file share (this is what subPath mounts to).
        var storageKey = await _secrets.GetStorageKeyAsync(workspaceId, ct);
        await _storage.EnsureUserDirectoryAsync(
            workspace.StorageAccountName,
            workspace.FileShareName,
            storageKey,
            userId,
            ct);

        var session = UserSession.Create(workspaceId, applicationId, userId);
        await _sessionRepo.AddAsync(session, ct);

        try
        {
            session.Status = SessionStatus.Starting;
            var result = await _k8s.LaunchSessionAsync(workspace, application, session, ct);

            session.AccessUrl = result.AccessUrl;
            session.StartedAtUtc = DateTime.UtcNow;
            await _sessionRepo.UpdateAsync(session, ct);

            _log.LogInformation(
                "Session {SessionId} launched: app={App} user={User} url={Url}",
                session.Id, application.Name, userId, result.AccessUrl);

            return session;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Session {SessionId} launch failed", session.Id);
            session.Status = SessionStatus.Failed;
            await _sessionRepo.UpdateAsync(session, ct);
            throw;
        }
    }

    public async Task StopAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await _sessionRepo.GetByIdAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Session {sessionId} not found");

        var workspace = await _workspaceRepo.GetByIdAsync(session.WorkspaceId, ct)
            ?? throw new InvalidOperationException("Workspace gone");

        session.Status = SessionStatus.Stopping;
        await _sessionRepo.UpdateAsync(session, ct);

        await _k8s.StopSessionAsync(workspace.K8sNamespace, session.DeploymentName, session.ServiceName, ct);

        session.Status = SessionStatus.Stopped;
        session.StoppedAtUtc = DateTime.UtcNow;
        await _sessionRepo.UpdateAsync(session, ct);
    }
}
