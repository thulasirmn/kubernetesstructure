using System;
using System.Collections.Generic;

namespace SRW.Domain.Entities;

/// <summary>
/// A Workspace is the top-level isolation boundary.
/// Each Workspace maps 1:1 with:
///   - One Azure Storage Account
///   - One File Share (shared across users in the workspace)
///   - One AKS namespace
///   - One Kubernetes Secret holding the storage account key (for CSI)
/// </summary>
public class Workspace
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = default!;
    public string Description { get; private set; } = default!;
    public WorkspaceStatus Status { get; private set; }

    // Azure resource pointers (populated after provisioning)
    public string ResourceGroup { get; private set; } = default!;
    public string StorageAccountName { get; private set; } = default!;
    public string FileShareName { get; private set; } = default!;
    public string K8sNamespace { get; private set; } = default!;

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ProvisionedAtUtc { get; private set; }

    // Navigation
    public List<WorkspaceUser> Users { get; private set; } = new();

    /// <summary>IDs of catalog applications assigned to this workspace.</summary>
    public List<Guid> ApplicationIds { get; private set; } = new();

    private Workspace() { }

    public long QuotaInGiB { get; private set; } = 100;

    public static Workspace Create(string name, string description, string resourceGroup, long quotaInGiB = 100)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Workspace name is required");

        return new Workspace
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            ResourceGroup = resourceGroup,
            Status = WorkspaceStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
            QuotaInGiB = quotaInGiB,
            StorageAccountName = $"srw{Guid.NewGuid().ToString("N")[..18]}".ToLowerInvariant(),
            FileShareName = "workspace-share",
            K8sNamespace = $"ws-{name.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid().ToString("N")[..6]}"
        };
    }

    public void MarkProvisioning() => Status = WorkspaceStatus.Provisioning;

    public void MarkProvisioned()
    {
        Status = WorkspaceStatus.Active;
        ProvisionedAtUtc = DateTime.UtcNow;
    }

    public void MarkFailed() => Status = WorkspaceStatus.Failed;

    public void MarkDeleting() => Status = WorkspaceStatus.Deleting;

    // Used by the persistence layer to rehydrate from storage without re-running creation logic.
    public static Workspace Reconstitute(
        Guid id, string name, string description, WorkspaceStatus status,
        string resourceGroup, string storageAccountName, string fileShareName,
        string k8sNamespace, DateTime createdAtUtc, DateTime? provisionedAtUtc,
        List<WorkspaceUser> users, List<Guid> applicationIds,
        long quotaInGiB = 100) =>
        new()
        {
            Id = id,
            Name = name,
            Description = description,
            Status = status,
            ResourceGroup = resourceGroup,
            StorageAccountName = storageAccountName,
            FileShareName = fileShareName,
            K8sNamespace = k8sNamespace,
            CreatedAtUtc = createdAtUtc,
            ProvisionedAtUtc = provisionedAtUtc,
            Users = users,
            ApplicationIds = applicationIds,
            QuotaInGiB = quotaInGiB
        };

    public void AddUser(string userId, string displayName, WorkspaceRole role)
    {
        if (Users.Exists(u => u.UserId == userId))
            throw new InvalidOperationException("User already in workspace");

        Users.Add(new WorkspaceUser
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Id,
            UserId = userId,
            DisplayName = displayName,
            Role = role,
            JoinedAtUtc = DateTime.UtcNow
        });
    }

    public void AssignApplication(Guid appId)
    {
        if (!ApplicationIds.Contains(appId))
            ApplicationIds.Add(appId);
    }

    public void UnassignApplication(Guid appId) => ApplicationIds.Remove(appId);
}

public enum WorkspaceStatus { Pending, Provisioning, Active, Failed, Deleting }
public enum WorkspaceRole { Admin, Researcher, Viewer }
