using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SRW.Domain.Entities;

namespace SRW.Core.Abstractions;

public interface IWorkspaceRepository
{
    Task<Workspace?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<Workspace>> ListForUserAsync(string userId, CancellationToken ct = default);
    Task<List<Workspace>> ListByStatusAsync(WorkspaceStatus status, CancellationToken ct = default);
    Task AddAsync(Workspace workspace, CancellationToken ct = default);
    Task UpdateAsync(Workspace workspace, CancellationToken ct = default);
    Task AssignApplicationAsync(Guid workspaceId, Guid appId, CancellationToken ct = default);
    Task UnassignApplicationAsync(Guid workspaceId, Guid appId, CancellationToken ct = default);
}

/// <summary>
/// CRUD for the global application catalog (Cosmos "applications" container, partition key /id).
/// Applications are not workspace-scoped here; workspaces hold a list of assigned app IDs.
/// </summary>
public interface IApplicationRepository
{
    Task<WorkspaceApplication?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<WorkspaceApplication>> ListAsync(bool includeDisabled = false, CancellationToken ct = default);
    Task<List<WorkspaceApplication>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
    Task AddAsync(WorkspaceApplication application, CancellationToken ct = default);
    Task UpdateAsync(WorkspaceApplication application, CancellationToken ct = default);
    Task DisableAsync(Guid id, CancellationToken ct = default);
}

public interface ISessionRepository
{
    Task<UserSession?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<UserSession?> GetActiveAsync(Guid workspaceId, Guid applicationId, string userId, CancellationToken ct = default);
    Task<List<UserSession>> ListForUserAsync(Guid workspaceId, string userId, CancellationToken ct = default);
    Task<List<UserSession>> ListByStatusAsync(SessionStatus status, CancellationToken ct = default);
    Task<List<UserSession>> ListIdleAsync(TimeSpan idleThreshold, CancellationToken ct = default);
    Task AddAsync(UserSession session, CancellationToken ct = default);
    Task UpdateAsync(UserSession session, CancellationToken ct = default);
    Task TouchAsync(Guid sessionId, Guid workspaceId, CancellationToken ct = default);
}

/// <summary>
/// Holds (and protects) the per-workspace storage account key.
/// Production: Azure Key Vault. Dev: encrypted column.
/// </summary>
public interface IWorkspaceSecretStore
{
    Task SetStorageKeyAsync(Guid workspaceId, string accountKey, CancellationToken ct = default);
    Task<string> GetStorageKeyAsync(Guid workspaceId, CancellationToken ct = default);
    Task DeleteAsync(Guid workspaceId, CancellationToken ct = default);
}
