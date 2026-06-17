using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using k8s;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SRW.Core.Abstractions;
using SRW.Domain.Entities;

namespace SRW.Infrastructure.Terraform;

/// <summary>
/// Drop-in replacement for both <see cref="Azure.AzureStorageProvisioner"/> and
/// <see cref="Kubernetes.KubernetesOrchestrator"/>. Workspace and session resources
/// are declared in Terraform modules; this class shells out to the Terraform CLI.
///
/// Per-workspace state:  workspaces/{storageAccountName}-storage.tfstate  (Azure storage)
///                       workspaces/{k8sNamespace}-k8s.tfstate             (K8s namespace)
/// Per-session state:    sessions/{sessionId}.tfstate
///
/// Read-only K8s operations (GetSessionStatus, ListManagedNamespaces) still use
/// the Kubernetes client directly — these are runtime state queries, not infra state.
/// </summary>
public sealed class TerraformOrchestrator : IAzureStorageProvisioner, IKubernetesOrchestrator
{
    private readonly TerraformRunner _runner;
    private readonly TerraformOptions _opts;
    private readonly IKubernetes _k8s;
    private readonly ILogger<TerraformOrchestrator> _log;

    public TerraformOrchestrator(
        TerraformRunner runner,
        IOptions<TerraformOptions> opts,
        ILogger<TerraformOrchestrator> log)
    {
        _runner = runner;
        _opts   = opts.Value;
        _log    = log;

        var config = KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile();
        _k8s = new k8s.Kubernetes(config);
    }

    // ── IAzureStorageProvisioner ──────────────────────────────────────────────

    public async Task<StorageProvisioningResult> ProvisionAsync(
        string resourceGroup,
        string storageAccountName,
        string fileShareName,
        long quotaInGiB,
        CancellationToken ct = default)
    {
        var stateKey    = $"workspaces/{storageAccountName}-storage.tfstate";
        var workingDir  = _runner.GetWorkingDir("workspace-storage", storageAccountName);

        _log.LogInformation("Provisioning storage for {Account} via Terraform", storageAccountName);

        await _runner.InitAsync(workingDir, "workspace-storage", stateKey, ct);
        await _runner.ApplyAsync(workingDir, new Dictionary<string, string>
        {
            ["workspace_id"]         = ExtractWorkspaceId(storageAccountName),
            ["resource_group"]       = resourceGroup,
            ["storage_account_name"] = storageAccountName,
            ["file_share_name"]      = fileShareName,
            ["quota_gib"]            = quotaInGiB.ToString(),
            ["region"]               = _opts.Region,
        }, ct: ct);

        var outputs = await _runner.OutputAsync(workingDir, ct);
        return new StorageProvisioningResult(
            StorageAccountName: outputs["storage_account_name"].GetString()!,
            FileShareName:      outputs["file_share_name"].GetString()!,
            AccountKey:         outputs["account_key"].GetString()!);
    }

    public async Task DeleteAsync(
        string resourceGroup,
        string storageAccountName,
        CancellationToken ct = default)
    {
        var workingDir = _runner.GetWorkingDir("workspace-storage", storageAccountName);
        _log.LogInformation("Destroying storage {Account} via Terraform", storageAccountName);
        await _runner.DestroyAsync(workingDir, ct);
    }

    // ── IKubernetesOrchestrator — workspace namespace ─────────────────────────

    public async Task EnsureWorkspaceNamespaceAsync(
        Workspace workspace,
        string storageAccountKey,
        CancellationToken ct = default)
    {
        var stateKey   = $"workspaces/{workspace.K8sNamespace}-k8s.tfstate";
        var workingDir = _runner.GetWorkingDir("workspace-k8s", workspace.K8sNamespace);

        _log.LogInformation("Provisioning K8s namespace {Ns} via Terraform", workspace.K8sNamespace);

        await _runner.InitAsync(workingDir, "workspace-k8s", stateKey, ct);
        await _runner.ApplyAsync(
            workingDir,
            vars: new Dictionary<string, string>
            {
                ["workspace_id"]         = workspace.Id.ToString(),
                ["k8s_namespace"]        = workspace.K8sNamespace,
                ["storage_account_name"] = workspace.StorageAccountName,
            },
            sensitiveEnvVars: new Dictionary<string, string>
            {
                // Passed as env var so the storage key is never written to terraform.tfvars on disk.
                ["TF_VAR_storage_account_key"] = storageAccountKey
            },
            ct: ct);
    }

    public async Task DeleteWorkspaceNamespaceAsync(string k8sNamespace, CancellationToken ct = default)
    {
        var workingDir = _runner.GetWorkingDir("workspace-k8s", k8sNamespace);
        _log.LogInformation("Destroying K8s namespace {Ns} via Terraform", k8sNamespace);
        await _runner.DestroyAsync(workingDir, ct);
    }

    // ── IKubernetesOrchestrator — session lifecycle ───────────────────────────

    public async Task<SessionDeploymentResult> LaunchSessionAsync(
        Workspace workspace,
        WorkspaceApplication application,
        UserSession session,
        CancellationToken ct = default)
    {
        var stateKey   = $"sessions/{session.Id}.tfstate";
        var workingDir = _runner.GetWorkingDir("session", session.Id.ToString());

        _log.LogInformation("Launching session {SessionId} via Terraform", session.Id);

        await _runner.InitAsync(workingDir, "session", stateKey, ct);
        await _runner.ApplyAsync(workingDir, new Dictionary<string, string>
        {
            ["session_id"]       = session.Id.ToString(),
            ["workspace_id"]     = workspace.Id.ToString(),
            ["user_id"]          = session.UserId,
            ["app_type"]         = application.Type.ToString().ToLowerInvariant(),
            ["k8s_namespace"]    = workspace.K8sNamespace,
            ["ingress_path"]     = session.IngressPath,
            ["ingress_domain"]   = _opts.IngressDomain,
            ["container_image"]  = application.ContainerImage,
            ["container_port"]   = application.ContainerPort.ToString(),
            ["cpu_request"]      = application.CpuRequest,
            ["cpu_limit"]        = application.CpuLimit,
            ["memory_request"]   = application.MemoryRequest,
            ["memory_limit"]     = application.MemoryLimit,
            ["mount_path"]       = application.MountPath,
            ["file_share_name"]  = workspace.FileShareName,
            ["environment_json"] = application.EnvironmentJson ?? "{}",
            ["command_json"]     = application.CommandJson ?? "[]",
        }, ct: ct);

        var outputs = await _runner.OutputAsync(workingDir, ct);
        return new SessionDeploymentResult(
            DeploymentName: outputs["deployment_name"].GetString()!,
            ServiceName:    outputs["service_name"].GetString()!,
            AccessUrl:      outputs["access_url"].GetString()!);
    }

    public async Task StopSessionAsync(
        string k8sNamespace,
        string deploymentName,
        string serviceName,
        Guid sessionId,
        CancellationToken ct = default)
    {
        var workingDir = _runner.GetWorkingDir("session", sessionId.ToString());
        _log.LogInformation("Stopping session {SessionId} via Terraform destroy", sessionId);
        await _runner.DestroyAsync(workingDir, ct);
    }

    // ── IKubernetesOrchestrator — read-only K8s status ───────────────────────
    // These are runtime state queries — Terraform state doesn't track pod readiness.

    public async Task<SessionStatus> GetSessionStatusAsync(
        string k8sNamespace,
        string deploymentName,
        CancellationToken ct = default)
    {
        try
        {
            var dep = await _k8s.AppsV1.ReadNamespacedDeploymentAsync(
                deploymentName, k8sNamespace, cancellationToken: ct);

            return dep.Status?.ReadyReplicas >= 1
                ? SessionStatus.Running
                : SessionStatus.Starting;
        }
        catch (k8s.Autorest.HttpOperationException e)
            when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return SessionStatus.Stopped;
        }
    }

    public async Task<List<string>> ListManagedNamespacesAsync(CancellationToken ct = default)
    {
        var list = await _k8s.CoreV1.ListNamespaceAsync(
            labelSelector: "srw.io/managed-by=srw-terraform",
            cancellationToken: ct);
        return list.Items.Select(ns => ns.Metadata.Name).ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Storage account name format: "srw" + workspaceId (no dashes).
    // Reversing this gives us the workspace ID for tagging.
    private static string ExtractWorkspaceId(string storageAccountName)
        => storageAccountName.StartsWith("srw", StringComparison.OrdinalIgnoreCase)
            ? storageAccountName[3..]
            : storageAccountName;
}
