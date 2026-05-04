using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SRW.Core.Abstractions;
using SRW.Domain.Entities;
using SRW.Infrastructure.Azure;

namespace SRW.Infrastructure.Kubernetes;

/// <summary>
/// Concrete K8s orchestrator. Talks to AKS via the official KubernetesClient SDK.
///
/// Per-workspace resources:
///   - Namespace
///   - Secret (azure-storage-creds) holding the storage account name + key for the CSI driver
///   - PersistentVolume + PersistentVolumeClaim referring to the workspace's File Share
///   - NetworkPolicy isolating the namespace from other workspaces
///
/// Per-session resources (one per running app per user):
///   - Deployment with subPath=userId on the shared PVC
///   - Service (ClusterIP)
///   - Ingress rule (host or path-based) → user accesses via https://&lt;domain&gt;/s/&lt;sessionSlug&gt;/
/// </summary>
public sealed class KubernetesOrchestrator : IKubernetesOrchestrator
{
    private readonly IKubernetes _client;
    private readonly AzureOptions _azureOptions;
    private readonly ILogger<KubernetesOrchestrator> _log;

    public KubernetesOrchestrator(
        IOptions<AzureOptions> azureOptions,
        ILogger<KubernetesOrchestrator> log)
    {
        _azureOptions = azureOptions.Value;
        _log = log;
        // Inside a pod: KubernetesClientConfiguration.InClusterConfig()
        // Locally: BuildConfigFromConfigFile()
        var config = KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile();
        _client = new k8s.Kubernetes(config);
    }

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
                    ["srw.io/managed-by"] = "srw-api"
                }
            }
        };
        try
        {
            await _client.CoreV1.CreateNamespaceAsync(ns, cancellationToken: ct);
        }
        catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Idempotent — namespace already exists.
        }

        // 2) Storage credentials Secret (the CSI driver reads this)
        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = "azure-storage-creds", NamespaceProperty = workspace.K8sNamespace },
            Type = "Opaque",
            Data = new Dictionary<string, byte[]>
            {
                ["azurestorageaccountname"] = Encoding.UTF8.GetBytes(workspace.StorageAccountName),
                ["azurestorageaccountkey"] = Encoding.UTF8.GetBytes(storageAccountKey)
            }
        };
        await UpsertSecretAsync(secret, ct);

        // 3) NetworkPolicy: deny all ingress except from the platform's ingress controller.
        //    Researchers in workspace A can't reach workspace B's pods.
        var policy = new V1NetworkPolicy
        {
            Metadata = new V1ObjectMeta { Name = "default-deny", NamespaceProperty = workspace.K8sNamespace },
            Spec = new V1NetworkPolicySpec
            {
                PodSelector = new V1LabelSelector(),
                PolicyTypes = new[] { "Ingress" },
                Ingress = new List<V1NetworkPolicyIngressRule>
                {
                    new()
                    {
                        From = new List<V1NetworkPolicyPeer>
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

    public async Task<SessionDeploymentResult> LaunchSessionAsync(
        Workspace workspace,
        WorkspaceApplication application,
        UserSession session,
        CancellationToken ct = default)
    {
        var ingressPath = session.IngressPath;        // e.g. "/s/abc123def0"
        var basePath = ingressPath + "/";
        var safeUserDir = SanitizeDirName(session.UserId);

        // ── Deployment ────────────────────────────────────────────────────────
        var labels = new Dictionary<string, string>
        {
            ["srw.io/session-id"] = session.Id.ToString(),
            ["srw.io/workspace-id"] = workspace.Id.ToString(),
            ["srw.io/user-id"] = SanitizeLabel(session.UserId),
            ["srw.io/app"] = application.Type.ToString().ToLowerInvariant(),
            ["app"] = session.DeploymentName
        };

        var envVars = ParseEnv(application.EnvironmentJson);
        var commandArr = ParseCommand(application.CommandJson, basePath);

        var container = new V1Container
        {
            Name = "app",
            Image = application.ContainerImage,
            ImagePullPolicy = "IfNotPresent",
            Ports = new List<V1ContainerPort>
            {
                new() { ContainerPort = application.ContainerPort, Name = "http" }
            },
            Env = envVars,
            Command = commandArr,
            Resources = new V1ResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity>
                {
                    ["cpu"] = new(application.CpuRequest),
                    ["memory"] = new(application.MemoryRequest)
                },
                Limits = new Dictionary<string, ResourceQuantity>
                {
                    ["cpu"] = new(application.CpuLimit),
                    ["memory"] = new(application.MemoryLimit)
                }
            },
            VolumeMounts = new List<V1VolumeMount>
            {
                new()
                {
                    Name = "workspace-share",
                    MountPath = application.MountPath,
                    SubPath = safeUserDir   // ← isolates each user's data on the shared share
                }
            },
            SecurityContext = new V1SecurityContext
            {
                AllowPrivilegeEscalation = false,
                ReadOnlyRootFilesystem = false,
                RunAsNonRoot = false  // RStudio image needs root; tighten per-image as needed
            }
        };

        // The Volume itself uses the Azure File CSI driver, pointing at the shared share.
        // Every user pod in this workspace mounts the same share — but at a different subPath.
        var volume = new V1Volume
        {
            Name = "workspace-share",
            Csi = new V1CSIVolumeSource
            {
                Driver = "file.csi.azure.com",
                ReadOnlyProperty = false,
                VolumeAttributes = new Dictionary<string, string>
                {
                    ["secretName"] = "azure-storage-creds",
                    ["shareName"] = workspace.FileShareName,
                    // mountOptions tuned for concurrent multi-pod access
                    ["mountOptions"] = "dir_mode=0777,file_mode=0777,uid=1000,gid=1000,mfsymlinks,cache=strict,nosharesock"
                }
            }
        };

        var deployment = new V1Deployment
        {
            Metadata = new V1ObjectMeta
            {
                Name = session.DeploymentName,
                NamespaceProperty = workspace.K8sNamespace,
                Labels = labels
            },
            Spec = new V1DeploymentSpec
            {
                Replicas = 1,
                Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["app"] = session.DeploymentName } },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = labels },
                    Spec = new V1PodSpec
                    {
                        Containers = new List<V1Container> { container },
                        Volumes = new List<V1Volume> { volume },
                        AutomountServiceAccountToken = false
                    }
                }
            }
        };
        await _client.AppsV1.CreateNamespacedDeploymentAsync(deployment, workspace.K8sNamespace, cancellationToken: ct);

        // ── Service ───────────────────────────────────────────────────────────
        var service = new V1Service
        {
            Metadata = new V1ObjectMeta { Name = session.ServiceName, NamespaceProperty = workspace.K8sNamespace, Labels = labels },
            Spec = new V1ServiceSpec
            {
                Selector = new Dictionary<string, string> { ["app"] = session.DeploymentName },
                Ports = new List<V1ServicePort>
                {
                    new() { Port = 80, TargetPort = application.ContainerPort, Protocol = "TCP" }
                },
                Type = "ClusterIP"
            }
        };
        await _client.CoreV1.CreateNamespacedServiceAsync(service, workspace.K8sNamespace, cancellationToken: ct);

        // ── Ingress (path-based routing under the platform domain) ────────────
        var ingress = new V1Ingress
        {
            Metadata = new V1ObjectMeta
            {
                Name = session.DeploymentName,
                NamespaceProperty = workspace.K8sNamespace,
                Labels = labels,
                Annotations = new Dictionary<string, string>
                {
                    ["nginx.ingress.kubernetes.io/proxy-read-timeout"] = "3600",
                    ["nginx.ingress.kubernetes.io/proxy-send-timeout"] = "3600",
                    // WebSocket support — Jupyter needs this; RStudio's Shiny too.
                    ["nginx.ingress.kubernetes.io/proxy-http-version"] = "1.1"
                }
            },
            Spec = new V1IngressSpec
            {
                IngressClassName = "nginx",
                Rules = new List<V1IngressRule>
                {
                    new()
                    {
                        Host = _azureOptions.IngressDomain,
                        Http = new V1HTTPIngressRuleValue
                        {
                            Paths = new List<V1HTTPIngressPath>
                            {
                                new()
                                {
                                    Path = ingressPath + "(/|$)(.*)",
                                    PathType = "ImplementationSpecific",
                                    Backend = new V1IngressBackend
                                    {
                                        Service = new V1IngressServiceBackend
                                        {
                                            Name = session.ServiceName,
                                            Port = new V1ServiceBackendPort { Number = 80 }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
        await _client.NetworkingV1.CreateNamespacedIngressAsync(ingress, workspace.K8sNamespace, cancellationToken: ct);

        var url = $"https://{_azureOptions.IngressDomain}{ingressPath}/";
        return new SessionDeploymentResult(session.DeploymentName, session.ServiceName, url);
    }

    public async Task<SessionStatus> GetSessionStatusAsync(string ns, string deploymentName, CancellationToken ct = default)
    {
        try
        {
            var dep = await _client.AppsV1.ReadNamespacedDeploymentAsync(deploymentName, ns, cancellationToken: ct);
            if (dep.Status?.ReadyReplicas >= 1) return SessionStatus.Running;
            if (dep.Status?.UnavailableReplicas >= 1) return SessionStatus.Starting;
            return SessionStatus.Starting;
        }
        catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return SessionStatus.Stopped;
        }
    }

    public async Task StopSessionAsync(string ns, string deploymentName, string serviceName, CancellationToken ct = default)
    {
        async Task SwallowNotFound(Func<Task> action)
        {
            try { await action(); }
            catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        }
        await SwallowNotFound(() => _client.NetworkingV1.DeleteNamespacedIngressAsync(deploymentName, ns, cancellationToken: ct));
        await SwallowNotFound(() => _client.CoreV1.DeleteNamespacedServiceAsync(serviceName, ns, cancellationToken: ct));
        await SwallowNotFound(() => _client.AppsV1.DeleteNamespacedDeploymentAsync(deploymentName, ns, cancellationToken: ct));
    }

    public async Task DeleteWorkspaceNamespaceAsync(string ns, CancellationToken ct = default)
    {
        try { await _client.CoreV1.DeleteNamespaceAsync(ns, cancellationToken: ct); }
        catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound) { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task UpsertSecretAsync(V1Secret secret, CancellationToken ct)
    {
        try { await _client.CoreV1.CreateNamespacedSecretAsync(secret, secret.Metadata.NamespaceProperty, cancellationToken: ct); }
        catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            await _client.CoreV1.ReplaceNamespacedSecretAsync(secret, secret.Metadata.Name, secret.Metadata.NamespaceProperty, cancellationToken: ct);
        }
    }

    private static List<V1EnvVar> ParseEnv(string json)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        var list = new List<V1EnvVar>(dict.Count);
        foreach (var kv in dict) list.Add(new V1EnvVar(kv.Key, kv.Value));
        return list;
    }

    private static IList<string>? ParseCommand(string? json, string basePath)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var arr = JsonSerializer.Deserialize<List<string>>(json) ?? new();
        for (var i = 0; i < arr.Count; i++)
        {
            // Replace token so Jupyter rewrites URLs correctly under our ingress prefix.
            arr[i] = arr[i].Replace("__BASE_URL__", basePath);
        }
        return arr;
    }

    private static string SanitizeLabel(string s)
    {
        // K8s labels: [a-z0-9A-Z._-]{1,63}
        var sb = new StringBuilder(Math.Min(63, s.Length));
        foreach (var c in s)
        {
            if (sb.Length == 63) break;
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-');
        }
        return sb.Length == 0 ? "user" : sb.ToString();
    }

    private static string SanitizeDirName(string userId)
    {
        var sb = new StringBuilder(Math.Min(255, userId.Length));
        foreach (var c in userId)
        {
            if (sb.Length == 255) break;
            sb.Append(c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
        }
        return sb.ToString();
    }
}
