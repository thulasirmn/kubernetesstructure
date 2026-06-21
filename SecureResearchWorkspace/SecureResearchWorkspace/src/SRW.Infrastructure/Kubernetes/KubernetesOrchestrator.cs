using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerService;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SRW.Core.Abstractions;
using SRW.Domain.Entities;
using SRW.Infrastructure.Azure;
using SRW.Infrastructure.Helm;

namespace SRW.Infrastructure.Kubernetes;

/// <summary>
/// Manages Kubernetes resources for the platform.
///
/// Workspace resources (one-time setup):
///   - Namespace with srw.io labels
///   - Secret "azure-storage-creds" (CSI driver reads this to mount the File Share)
///   - Default-deny NetworkPolicy
///
/// Session resources (Helm — see charts/session/):
///   - Deployment (one pod per user session)
///   - ClusterIP Service
///   - Ingress rule (/s/<slug>)
/// </summary>
public sealed class KubernetesOrchestrator : IKubernetesOrchestrator
{
    private readonly IKubernetes _client;
    private readonly HelmRunner _helm;
    private readonly AzureOptions _azureOptions;
    private readonly ILogger<KubernetesOrchestrator> _log;

    public KubernetesOrchestrator(
        HelmRunner helm,
        IOptions<AzureOptions> azureOptions,
        ILogger<KubernetesOrchestrator> log)
    {
        _helm         = helm;
        _azureOptions = azureOptions.Value;
        _log          = log;

        var config = BuildK8sConfigAsync(_azureOptions, CancellationToken.None)
            .GetAwaiter().GetResult();
        _client = new k8s.Kubernetes(config);
    }

    /// <summary>
    /// Resolves K8s client config for three deployment environments:
    ///   1. Inside a K8s pod (AKS production)   → in-cluster service account token
    ///   2. Local dev                            → ~/.kube/config
    ///   3. App Service / any host without file  → fetch kubeconfig from AKS via ARM + Managed Identity
    /// </summary>
    private static async Task<KubernetesClientConfiguration> BuildK8sConfigAsync(
        AzureOptions opts, CancellationToken ct)
    {
        // 1. Running inside a K8s pod
        if (KubernetesClientConfiguration.IsInCluster())
            return KubernetesClientConfiguration.InClusterConfig();

        // 2. Local dev — kubeconfig file present
        var kubeconfigPath = KubernetesClientConfiguration.KubeConfigDefaultLocation;
        if (File.Exists(kubeconfigPath))
            return KubernetesClientConfiguration.BuildConfigFromConfigFile();

        // 3. App Service or any host without a local kubeconfig
        //    Fetch user credentials from AKS via the ARM API using Managed Identity.
        //    Requires: Managed Identity on the host + "AKS RBAC Cluster Admin" role on the cluster.
        if (string.IsNullOrWhiteSpace(opts.SubscriptionId) ||
            string.IsNullOrWhiteSpace(opts.AksClusterName) ||
            string.IsNullOrWhiteSpace(opts.AksResourceGroup))
        {
            throw new InvalidOperationException(
                "No kubeconfig found and Azure:SubscriptionId / AksClusterName / AksResourceGroup " +
                "are not set. Configure these so the app can fetch AKS credentials via Managed Identity.");
        }

        var arm     = new ArmClient(new DefaultAzureCredential());
        var aksId   = ContainerServiceManagedClusterResource.CreateResourceIdentifier(
                          opts.SubscriptionId, opts.AksResourceGroup, opts.AksClusterName);
        var cluster = arm.GetContainerServiceManagedClusterResource(aksId);
        var creds   = await cluster.GetClusterUserCredentialsAsync(cancellationToken: ct);
        var yaml    = Encoding.UTF8.GetString(creds.Value.Kubeconfigs[0].Value);

        // BuildConfigFromConfigFile only accepts a file path, not a string — write to a temp file.
        var tempPath = Path.Combine(Path.GetTempPath(), $"srw-kubeconfig-{Guid.NewGuid():N}.yaml");
        try
        {
            await File.WriteAllTextAsync(tempPath, yaml, ct);
            return KubernetesClientConfiguration.BuildConfigFromConfigFile(tempPath);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    // ── Workspace namespace setup (K8s SDK) ───────────────────────────────────

    public async Task EnsureWorkspaceNamespaceAsync(
        Workspace workspace,
        string storageAccountKey,
        CancellationToken ct = default)
    {
        // 1) Namespace
        var ns = new V1Namespace
        {
            Metadata = new V1ObjectMeta
            {
                Name = workspace.K8sNamespace,
                Labels = new Dictionary<string, string>
                {
                    ["srw.io/workspace-id"] = workspace.Id.ToString(),
                    ["srw.io/managed-by"]   = "srw-api"
                }
            }
        };
        try { await _client.CoreV1.CreateNamespaceAsync(ns, cancellationToken: ct); }
        catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.Conflict) { }

        // 2) Storage credentials Secret — CSI driver reads this to mount the Azure File Share
        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name              = "azure-storage-creds",
                NamespaceProperty = workspace.K8sNamespace
            },
            Type = "Opaque",
            Data = new Dictionary<string, byte[]>
            {
                ["azurestorageaccountname"] = Encoding.UTF8.GetBytes(workspace.StorageAccountName),
                ["azurestorageaccountkey"]  = Encoding.UTF8.GetBytes(storageAccountKey)
            }
        };
        await UpsertSecretAsync(secret, ct);

        // 3) NetworkPolicy: deny all ingress except from the ingress-nginx namespace
        var policy = new V1NetworkPolicy
        {
            Metadata = new V1ObjectMeta
            {
                Name              = "default-deny",
                NamespaceProperty = workspace.K8sNamespace
            },
            Spec = new V1NetworkPolicySpec
            {
                PodSelector = new V1LabelSelector(),
                PolicyTypes = new[] { "Ingress" },
                Ingress = new List<V1NetworkPolicyIngressRule>
                {
                    new()
                    {
                        FromProperty = new List<V1NetworkPolicyPeer>
                        {
                            new() { NamespaceSelector = new V1LabelSelector
                            {
                                MatchLabels = new Dictionary<string, string> { ["name"] = "ingress-nginx" }
                            }}
                        }
                    }
                }
            }
        };
        try { await _client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(policy, workspace.K8sNamespace, cancellationToken: ct); }
        catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.Conflict) { }

        _log.LogInformation("K8s namespace {Ns} ready for workspace {WsId}", workspace.K8sNamespace, workspace.Id);
    }

    public async Task DeleteWorkspaceNamespaceAsync(string ns, CancellationToken ct = default)
    {
        try { await _client.CoreV1.DeleteNamespaceAsync(ns, cancellationToken: ct); }
        catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound) { }
    }

    public async Task<List<string>> ListManagedNamespacesAsync(CancellationToken ct = default)
    {
        var list = await _client.CoreV1.ListNamespaceAsync(
            labelSelector: "srw.io/managed-by=srw-api",
            cancellationToken: ct);
        return list.Items.Select(ns => ns.Metadata.Name).ToList();
    }

    // ── Session lifecycle (Helm) ──────────────────────────────────────────────

    public async Task<SessionDeploymentResult> LaunchSessionAsync(
        Workspace workspace,
        WorkspaceApplication application,
        UserSession session,
        CancellationToken ct = default)
    {
        // DeploymentName and ServiceName are pre-set in UserSession.Create — deterministic,
        // derived from the session ID. No need to parse output from Helm.
        var deploymentName  = session.DeploymentName;
        var serviceName     = session.ServiceName;
        var ingressPath     = session.IngressPath;
        var sanitizedUserId = SanitizeLabel(session.UserId);

        var isIpDomain = System.Net.IPAddress.TryParse(_azureOptions.IngressDomain, out _);
        var accessUrl  = isIpDomain
            ? $"http://{_azureOptions.IngressDomain}{ingressPath}/"
            : $"https://{_azureOptions.IngressDomain}{ingressPath}/";

        var values = new SessionHelmValues
        {
            SessionId       = session.Id.ToString(),
            WorkspaceId     = workspace.Id.ToString(),
            UserId          = session.UserId,
            SanitizedUserId = sanitizedUserId,
            AppType         = application.Type.ToString().ToLowerInvariant(),
            DeploymentName  = deploymentName,
            ServiceName     = serviceName,
            IngressPath     = ingressPath,
            IngressDomain   = _azureOptions.IngressDomain,
            ContainerImage  = application.ContainerImage,
            ContainerPort   = application.ContainerPort,
            CpuRequest      = application.CpuRequest,
            CpuLimit        = application.CpuLimit,
            MemoryRequest   = application.MemoryRequest,
            MemoryLimit     = application.MemoryLimit,
            MountPath       = application.MountPath,
            FileShareName   = workspace.FileShareName,
            Env             = ParseEnvDict(application.EnvironmentJson),
            Command         = BuildCommandList(application, ingressPath)
        };

        var json = JsonSerializer.Serialize(values, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented        = true
        });

        _log.LogInformation("Launching session {SessionId} via Helm release {Release} in {Ns}",
            session.Id, deploymentName, workspace.K8sNamespace);

        await _helm.InstallOrUpgradeAsync(deploymentName, workspace.K8sNamespace, json, ct);

        return new SessionDeploymentResult(deploymentName, serviceName, accessUrl);
    }

    public async Task StopSessionAsync(
        string k8sNamespace,
        string deploymentName,
        string serviceName,
        Guid sessionId,
        CancellationToken ct = default)
    {
        // Release name == deploymentName ("sess-<8-char-hex>") — set in UserSession.Create.
        _log.LogInformation("Stopping session {SessionId} via Helm uninstall of {Release}", sessionId, deploymentName);
        await _helm.UninstallAsync(deploymentName, k8sNamespace, ct);
    }

    // ── Pod activity sampling (K8s exec — bypasses NetworkPolicy) ────────────

    public async Task<long?> GetPodRxBytesAsync(
        string k8sNamespace,
        string deploymentName,
        CancellationToken ct = default)
    {
        try
        {
            var pods = await _client.CoreV1.ListNamespacedPodAsync(
                k8sNamespace,
                labelSelector: $"app={deploymentName}",
                cancellationToken: ct);

            var pod = pods.Items.FirstOrDefault(p => p.Status?.Phase == "Running");
            if (pod is null) return null;

            var outputBuffer = new MemoryStream();
            await _client.NamespacedPodExecAsync(
                pod.Metadata.Name,
                k8sNamespace,
                "app",
                new[] { "cat", "/sys/class/net/eth0/statistics/rx_bytes" },
                tty: false,
                async (_, stdout, _) => await stdout.CopyToAsync(outputBuffer, ct),
                ct);

            var text = Encoding.UTF8.GetString(outputBuffer.ToArray()).Trim();
            return long.TryParse(text, out var bytes) ? bytes : null;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "GetPodRxBytesAsync failed for {Deploy} in {Ns}", deploymentName, k8sNamespace);
            return null;
        }
    }

    // ── Session status (K8s SDK — reads live pod state, not Helm state) ───────

    public async Task<SessionStatus> GetSessionStatusAsync(
        string k8sNamespace,
        string deploymentName,
        CancellationToken ct = default)
    {
        try
        {
            var dep = await _client.AppsV1.ReadNamespacedDeploymentAsync(
                deploymentName, k8sNamespace, cancellationToken: ct);
            return dep.Status?.ReadyReplicas >= 1 ? SessionStatus.Running : SessionStatus.Starting;
        }
        catch (k8s.Autorest.HttpOperationException e)
            when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return SessionStatus.Stopped;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task UpsertSecretAsync(V1Secret secret, CancellationToken ct)
    {
        try
        {
            await _client.CoreV1.CreateNamespacedSecretAsync(
                secret, secret.Metadata.NamespaceProperty, cancellationToken: ct);
        }
        catch (k8s.Autorest.HttpOperationException e)
            when (e.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            await _client.CoreV1.ReplaceNamespacedSecretAsync(
                secret, secret.Metadata.Name, secret.Metadata.NamespaceProperty, cancellationToken: ct);
        }
    }

    private static Dictionary<string, string> ParseEnvDict(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(); }
        catch { return new(); }
    }

    /// <summary>
    /// Builds the container command list for custom apps and Jupyter (with __BASE_URL__ substitution).
    /// Returns an empty list for RStudio — the Helm template injects the www-root-path command.
    /// Returns an empty list for Jupyter with no CommandJson — the Helm template applies the default.
    /// </summary>
    private static List<string> BuildCommandList(WorkspaceApplication application, string ingressPath)
    {
        if (application.Type == ApplicationType.RStudio)
            return new();

        if (string.IsNullOrWhiteSpace(application.CommandJson))
            return new();

        try
        {
            var arr = JsonSerializer.Deserialize<List<string>>(application.CommandJson) ?? new();
            var basePath = ingressPath + "/";
            for (var i = 0; i < arr.Count; i++)
                arr[i] = arr[i].Replace("__BASE_URL__", basePath);
            return arr;
        }
        catch { return new(); }
    }

    private static string SanitizeLabel(string s)
    {
        var sb = new StringBuilder(Math.Min(63, s.Length));
        foreach (var c in s)
        {
            if (sb.Length == 63) break;
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-');
        }
        return sb.Length == 0 ? "user" : sb.ToString();
    }

    // ── Value model serialised as JSON to the Helm values file ───────────────

    private sealed class SessionHelmValues
    {
        public string SessionId       { get; init; } = "";
        public string WorkspaceId     { get; init; } = "";
        public string UserId          { get; init; } = "";
        public string SanitizedUserId { get; init; } = "";
        public string AppType         { get; init; } = "";
        public string DeploymentName  { get; init; } = "";
        public string ServiceName     { get; init; } = "";
        public string IngressPath     { get; init; } = "";
        public string IngressDomain   { get; init; } = "";
        public string ContainerImage  { get; init; } = "";
        public int    ContainerPort   { get; init; }
        public string CpuRequest      { get; init; } = "";
        public string CpuLimit        { get; init; } = "";
        public string MemoryRequest   { get; init; } = "";
        public string MemoryLimit     { get; init; } = "";
        public string MountPath       { get; init; } = "";
        public string FileShareName   { get; init; } = "";
        public Dictionary<string, string> Env     { get; init; } = new();
        public List<string>               Command { get; init; } = new();
    }
}
