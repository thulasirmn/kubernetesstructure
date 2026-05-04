using System.Threading;
using System.Threading.Tasks;
using SRW.Domain.Entities;

namespace SRW.Core.Abstractions;

/// <summary>
/// Orchestrates Kubernetes resources for the platform.
/// Implementation lives in SRW.Infrastructure and uses the official KubernetesClient SDK.
/// </summary>
public interface IKubernetesOrchestrator
{
    /// <summary>
    /// Creates a namespace for the workspace and stores the storage account key as a Secret
    /// the CSI driver (file.csi.azure.com) can use to mount the file share.
    /// </summary>
    Task EnsureWorkspaceNamespaceAsync(
        Workspace workspace,
        string storageAccountKey,
        CancellationToken ct = default);

    /// <summary>
    /// Launches a per-user pod. Returns once the deployment is *created* (not yet ready).
    /// The caller polls status separately or wires up a watcher.
    /// </summary>
    Task<SessionDeploymentResult> LaunchSessionAsync(
        Workspace workspace,
        WorkspaceApplication application,
        UserSession session,
        CancellationToken ct = default);

    Task<SessionStatus> GetSessionStatusAsync(
        string k8sNamespace,
        string deploymentName,
        CancellationToken ct = default);

    Task StopSessionAsync(
        string k8sNamespace,
        string deploymentName,
        string serviceName,
        CancellationToken ct = default);

    Task DeleteWorkspaceNamespaceAsync(string k8sNamespace, CancellationToken ct = default);

    /// <summary>
    /// Returns all namespace names that carry the srw.io/managed-by=srw-api label.
    /// Used by OrphanResourceCleaner to detect abandoned namespaces.
    /// </summary>
    Task<List<string>> ListManagedNamespacesAsync(CancellationToken ct = default);
}

public record SessionDeploymentResult(string DeploymentName, string ServiceName, string AccessUrl);
