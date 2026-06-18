# New Machine Setup — Helm & Service Bus Configuration

Companion to [`NEW_MACHINE_SETUP.md`](NEW_MACHINE_SETUP.md).
Covers the exact `appsettings.Development.json` values needed for Helm-backed session
launch and the Azure Service Bus queues.

---

## 1. Service Bus

Add this block to `src/SRW.Api/appsettings.Development.json`:

```json
"ServiceBus": {
  "FullyQualifiedNamespace": "srw-servicebus-dev.servicebus.windows.net",
  "ConnectionString": "Endpoint=sb://srw-servicebus-dev.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=<GET-FROM-AZURE-PORTAL-OR-TEAM>",
  "WorkspaceProvisionQueue": "srw-workspace-provision",
  "SessionStopQueue": "srw-session-stop",
  "WorkspaceCleanupQueue": "srw-workspace-cleanup"
}
```

> **Secret:** The `SharedAccessKey` value must be retrieved from the Azure Portal
> (`srw-servicebus-dev` namespace → Shared access policies → RootManageSharedAccessKey → Primary key)
> or from your team's secret store. **Never commit the real key to the repo.**
>
> Alternatively, omit `ConnectionString` entirely and use `az login` with the
> **Azure Service Bus Data Owner** role on the namespace — `DefaultAzureCredential`
> will pick it up automatically.

---

## 2. Helm

Session resources (Deployment, Service, Ingress) are provisioned by the **Helm CLI** via the
`charts/session/` chart at the repo root.

Add this block to `src/SRW.Api/appsettings.Development.json`:

```json
"Helm": {
  "HelmBinaryPath": "helm",
  "SessionChartPath": "C:/absolute/path/to/charts/session"
}
```

| Key | Notes |
|---|---|
| `HelmBinaryPath` | Path to the `helm` binary. If Helm is on your `PATH`, `"helm"` works. Otherwise use a full path like `C:/tools/helm/helm.exe`. |
| `SessionChartPath` | **Must be an absolute path** for local dev. When running `dotnet run --project src/SRW.Api`, the working directory is `src/SRW.Api` — the default relative value `"charts/session"` will not resolve. Use the full path, e.g. `"C:/dev/SecureResearchWorkspace/SecureResearchWorkspace/charts/session"`. |

### Verify Helm is reachable

```powershell
helm version
helm list --all-namespaces
```

### Troubleshooting sessions

```powershell
# List all running session releases in a workspace namespace
helm list -n ws-<name>-<id>

# Manually uninstall a stuck session release
helm uninstall sess-<8-hex> -n ws-<name>-<id>

# Check pod status for a session
kubectl get pods -n ws-<name>-<id>
kubectl describe pod <pod-name> -n ws-<name>-<id>
```

If a session is stuck in `Starting` but the Helm release is missing, call
`DELETE /api/workspaces/{id}/sessions/{sessionId}` to mark it Stopped in Cosmos.

---

## 3. Azure RBAC for storage provisioning

The identity running the API (your `az login` account locally) needs permission to create
storage accounts in the workspace resource group:

```
Storage Account Contributor  on  <resource-group>
```

This is required by `AzureStorageProvisioner` when calling `POST /api/workspaces`.

---

## 4. Complete `appsettings.Development.json` template

```json
{
  "Cosmos": {
    "Endpoint": "https://srw-cosmos-dev.documents.azure.com:443/",
    "AccountKey": "<COSMOS-KEY-OR-LEAVE-BLANK-FOR-MANAGED-IDENTITY>"
  },

  "Azure": {
    "SubscriptionId": "<AZURE-SUBSCRIPTION-ID>",
    "Region": "eastus",
    "AksClusterName": "srw-aks-dev",
    "AksResourceGroup": "srw-dev-rg",
    "IngressDomain": "<your-ingress-public-ip-or-domain>",
    "AllowedSubnetIds": []
  },

  "ServiceBus": {
    "FullyQualifiedNamespace": "srw-servicebus-dev.servicebus.windows.net",
    "ConnectionString": "Endpoint=sb://srw-servicebus-dev.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=<KEY>",
    "WorkspaceProvisionQueue": "srw-workspace-provision",
    "SessionStopQueue": "srw-session-stop",
    "WorkspaceCleanupQueue": "srw-workspace-cleanup"
  },

  "Helm": {
    "HelmBinaryPath": "helm",
    "SessionChartPath": "C:/absolute/path/to/SecureResearchWorkspace/SecureResearchWorkspace/charts/session"
  },

  "BackgroundJobs": {
    "SessionStatusPollSeconds": 15,
    "IdleReaperIntervalMinutes": 10,
    "IdleSessionThresholdHours": 8,
    "OrphanCleanerIntervalHours": 24,
    "ProvisioningMaxConcurrentCalls": 2,
    "ProvisioningMaxAutoLockRenewMinutes": 10
  }
}
```

> `appsettings.Development.json` is in `.gitignore` — never commit this file.
