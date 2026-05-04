using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SRW.Core.Abstractions;
using SRW.Domain.Entities;

namespace SRW.Core.Services;

/// <summary>
/// Orchestrates the multi-step provisioning of a workspace:
///   1. Create DB record (Pending)
///   2. Provision Azure Storage Account + File Share
///   3. Stash account key in secret store + K8s Secret
///   4. Create K8s namespace + RBAC
///   5. Mark Active
///
/// Each step is idempotent so the saga can be retried.
/// </summary>
public sealed class WorkspaceProvisioningService
{
    private readonly IWorkspaceRepository _repo;
    private readonly IAzureStorageProvisioner _storage;
    private readonly IKubernetesOrchestrator _k8s;
    private readonly IWorkspaceSecretStore _secrets;
    private readonly ILogger<WorkspaceProvisioningService> _log;

    public WorkspaceProvisioningService(
        IWorkspaceRepository repo,
        IAzureStorageProvisioner storage,
        IKubernetesOrchestrator k8s,
        IWorkspaceSecretStore secrets,
        ILogger<WorkspaceProvisioningService> log)
    {
        _repo = repo;
        _storage = storage;
        _k8s = k8s;
        _secrets = secrets;
        _log = log;
    }

    public async Task<Workspace> CreateAsync(
        CreateWorkspaceRequest request,
        CancellationToken ct = default)
    {
        var workspace = Workspace.Create(request.Name, request.Description, request.ResourceGroup);

        // Seed default application catalog: Jupyter + RStudio.
        // Custom images can be added later via API.
        workspace.Applications.Add(new WorkspaceApplication
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            Name = "Jupyter Notebook",
            Type = ApplicationType.Jupyter,
            ContainerImage = "jupyter/scipy-notebook:latest",
            ContainerPort = 8888,
            MountPath = "/home/jovyan/work",
            CommandJson = """["start-notebook.sh","--NotebookApp.token=''","--NotebookApp.password=''","--NotebookApp.base_url=__BASE_URL__"]"""
        });

        workspace.Applications.Add(new WorkspaceApplication
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            Name = "RStudio Server",
            Type = ApplicationType.RStudio,
            ContainerImage = "rocker/rstudio:latest",
            ContainerPort = 8787,
            MountPath = "/home/rstudio/work",
            EnvironmentJson = """{"DISABLE_AUTH":"true","ROOT":"true"}"""
        });

        await _repo.AddAsync(workspace, ct);
        _log.LogInformation("Workspace {Id} created (Pending) — name={Name}", workspace.Id, workspace.Name);

        try
        {
            workspace.GetType()
                .GetProperty(nameof(Workspace.Status))!
                .SetValue(workspace, WorkspaceStatus.Provisioning);
            await _repo.UpdateAsync(workspace, ct);

            // 1) Azure Storage
            var storageResult = await _storage.ProvisionAsync(
                workspace.ResourceGroup,
                workspace.StorageAccountName,
                workspace.FileShareName,
                request.QuotaInGiB,
                ct);

            await _secrets.SetStorageKeyAsync(workspace.Id, storageResult.AccountKey, ct);

            // 2) K8s namespace + Secret for CSI driver
            await _k8s.EnsureWorkspaceNamespaceAsync(workspace, storageResult.AccountKey, ct);

            // 3) Done
            workspace.MarkProvisioned();
            await _repo.UpdateAsync(workspace, ct);

            _log.LogInformation("Workspace {Id} provisioned successfully", workspace.Id);
            return workspace;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Workspace {Id} provisioning failed", workspace.Id);
            workspace.MarkFailed();
            await _repo.UpdateAsync(workspace, ct);
            throw;
        }
    }
}

public record CreateWorkspaceRequest(
    string Name,
    string Description,
    string ResourceGroup,
    long QuotaInGiB = 100);
