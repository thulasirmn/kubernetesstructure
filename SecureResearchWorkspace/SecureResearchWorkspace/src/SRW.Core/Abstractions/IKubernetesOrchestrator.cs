using System;
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
        Guid sessionId,
        CancellationToken ct = default);

    Task DeleteWorkspaceNamespaceAsync(string k8sNamespace, CancellationToken ct = default);

    /// <summary>
    /// Returns all namespace names that carry the srw.io/managed-by=srw-api label.
    /// Used by OrphanResourceCleaner to detect abandoned namespaces.
    /// </summary>
    Task<List<string>> ListManagedNamespacesAsync(CancellationToken ct = default);

    /// <summary>
    /// Reads cumulative bytes received on eth0 from inside the running pod for the given deployment,
    /// using the K8s exec API (bypasses NetworkPolicy — traffic goes through the API server).
    /// Returns null if no running pod is found or the exec fails.
    /// </summary>
    Task<long?> GetPodRxBytesAsync(string k8sNamespace, string deploymentName, CancellationToken ct = default);

    /// <summary>
    /// Creates or replaces a K8s Secret containing blob storage credentials for the CSI driver.
    /// Secret name follows the pattern blob-creds-{mountId}.
    /// </summary>
    Task EnsureBlobCredentialSecretAsync(
        string k8sNamespace,
        string secretName,
        string storageAccountName,
        string storageAccountKey,
        CancellationToken ct = default);

    Task DeleteBlobCredentialSecretAsync(string k8sNamespace, string secretName, CancellationToken ct = default);

    /// <summary>
    /// Creates a PersistentVolume (cluster-scoped) + PersistentVolumeClaim (namespace-scoped)
    /// for a blob container mount. PV mountOptions include uid=1000,gid=100,allow_other so the
    /// application user (jovyan/rstudio) can access the blobfuse2 filesystem — not possible
    /// with inline ephemeral CSI volumes which forbid uid/gid mount options.
    /// </summary>
    Task EnsureBlobPvcAsync(
        string k8sNamespace,
        string mountId,
        string storageAccountName,
        string containerName,
        string secretName,
        CancellationToken ct = default);

    Task DeleteBlobPvcAsync(string k8sNamespace, string mountId, CancellationToken ct = default);
}

public record SessionDeploymentResult(string DeploymentName, string ServiceName, string AccessUrl);
