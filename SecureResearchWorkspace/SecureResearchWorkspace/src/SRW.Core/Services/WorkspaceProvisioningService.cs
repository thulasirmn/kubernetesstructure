using Microsoft.Extensions.Logging;
using SRW.Core.Abstractions;
using SRW.Domain.Entities;

namespace SRW.Core.Services;

/// <summary>
/// Executes the multi-step provisioning and cleanup sagas for a workspace.
/// Called exclusively from background consumers — never from HTTP request handlers.
/// Each step is idempotent so a saga can be safely retried on failure.
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
        _repo    = repo;
        _storage = storage;
        _k8s     = k8s;
        _secrets = secrets;
        _log     = log;
    }

    public async Task ProvisionAsync(Guid workspaceId, CancellationToken ct = default)
    {
        var workspace = await _repo.GetByIdAsync(workspaceId, ct)
            ?? throw new InvalidOperationException($"Workspace {workspaceId} not found");

        if (workspace.Status == WorkspaceStatus.Active)
        {
            _log.LogInformation("Workspace {Id} is already Active — skipping", workspaceId);
            return;
        }

        workspace.MarkProvisioning();
        await _repo.UpdateAsync(workspace, ct);

        try
        {
            // 1) Azure Storage Account + File Share
            var storageResult = await _storage.ProvisionAsync(
                workspace.ResourceGroup,
                workspace.StorageAccountName,
                workspace.FileShareName,
                workspace.QuotaInGiB,
                ct);

            // 2) Encrypt and persist storage key
            await _secrets.SetStorageKeyAsync(workspace.Id, storageResult.AccountKey, ct);

            // 3) K8s namespace, credentials Secret, and NetworkPolicy
            await _k8s.EnsureWorkspaceNamespaceAsync(workspace, storageResult.AccountKey, ct);

            // 4) Mark Active
            workspace.MarkProvisioned();
            await _repo.UpdateAsync(workspace, ct);

            _log.LogInformation("Workspace {Id} provisioned successfully", workspaceId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Workspace {Id} provisioning failed", workspaceId);
            workspace.MarkFailed();
            await _repo.UpdateAsync(workspace, ct);
            throw;
        }
    }

    public async Task CleanupAsync(Guid workspaceId, CancellationToken ct = default)
    {
        var workspace = await _repo.GetByIdAsync(workspaceId, ct);
        if (workspace is null)
        {
            _log.LogWarning("Workspace {Id} not found during cleanup — treating as already deleted", workspaceId);
            return;
        }

        _log.LogInformation("Cleaning up workspace {Id}", workspaceId);

        // 1) Delete K8s namespace (cascades to all Deployments, Services, Ingresses, Secrets, Policies).
        await _k8s.DeleteWorkspaceNamespaceAsync(workspace.K8sNamespace, ct);

        // 2) Delete Azure Storage Account (cascades to File Shares).
        await _storage.DeleteAsync(workspace.ResourceGroup, workspace.StorageAccountName, ct);

        // 3) Remove the encrypted key.
        await _secrets.DeleteAsync(workspace.Id, ct);

        // 4) Mark Deleting so the workspace appears removed to callers.
        workspace.MarkDeleting();
        await _repo.UpdateAsync(workspace, ct);

        _log.LogInformation("Workspace {Id} cleaned up", workspaceId);
    }
}

public record CreateWorkspaceRequest(
    string Name,
    string Description,
    string ResourceGroup,
    long QuotaInGiB = 100);
