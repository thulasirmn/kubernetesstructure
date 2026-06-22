# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**Secure Research Workspace (SRW)** — a multi-tenant research platform on Azure Kubernetes Service (AKS). It provisions isolated workspaces where researchers can run Jupyter notebooks, RStudio, or custom Docker applications, sharing underlying Azure File Shares over SMB. Workspaces also support read-only mounting of Azure Blob containers (datasets) directly into sessions via the Azure Blob CSI driver.

## Common Commands

All commands run from `SecureResearchWorkspace/SecureResearchWorkspace/`:

```bash
# Restore and build
dotnet restore
dotnet build

# Run the API locally (Swagger at http://localhost:5000/swagger)
dotnet run --project src/SRW.Api

# Apply Kubernetes manifests
kubectl apply -f k8s/manifests/00-cluster-setup.yaml
kubectl apply -f k8s/manifests/10-api-deployment.yaml
```

No test project exists yet. No migrations needed — Cosmos DB containers are created automatically at startup via `CosmosContainerProvider.InitializeAsync()`.

**Running on a new/local machine:** see [`NEW_MACHINE_SETUP.md`](NEW_MACHINE_SETUP.md) — tooling, `appsettings.Development.json` config keys, Azure RBAC, and the Data Protection key-ring gotcha.

## Architecture

Clean Architecture with strict dependency direction: **Api → Infrastructure → Core → Domain**

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `SRW.Domain` | Entities only (`Workspace`, `UserSession`, `WorkspaceApplication`, `WorkspaceUser`). No external dependencies. |
| Core | `SRW.Core` | Application services and interface abstractions (`IKubernetesOrchestrator`, `IAzureStorageProvisioner`, `IWorkspaceRepository`, `ISessionRepository`). |
| Infrastructure | `SRW.Infrastructure` | Azure SDK (`AzureStorageProvisioner`), K8s SDK + Helm CLI (`KubernetesOrchestrator`), Cosmos DB (`Repositories`). No Terraform. |
| API | `SRW.Api` | ASP.NET Core Minimal API endpoints, DI composition root, auth middleware (`CurrentUser` reads `X-User-Id` header — Keycloak OIDC is deferred). |

## Infrastructure Stack

**No Terraform.** All provisioning uses native SDKs and Helm:

| Concern | Implementation | Files |
|---|---|---|
| Azure Storage Account + File Share | Azure ARM SDK (`DefaultAzureCredential`) | `src/SRW.Infrastructure/Azure/AzureStorageProvisioner.cs` |
| K8s Namespace + Secret + NetworkPolicy | K8s client SDK | `src/SRW.Infrastructure/Kubernetes/KubernetesOrchestrator.cs` |
| Session Deployment + Service + Ingress | Helm CLI (`helm upgrade --install`) | `src/SRW.Infrastructure/Helm/HelmRunner.cs`, `charts/session/` |
| Blob Container Mount (read-only datasets) | Azure ARM SDK (key fetch) + K8s SDK (PV/PVC/Secret) | `src/SRW.Infrastructure/Kubernetes/KubernetesOrchestrator.cs`, `src/SRW.Api/Endpoints/WorkspaceEndpoints.cs` |

## Key Flows

**Workspace Provisioning** (`POST /api/workspaces` → `WorkspaceProvisioningService.ProvisionAsync`):
1. DB record created (Pending)
2. `AzureStorageProvisioner.ProvisionAsync` — creates Storage Account + File Share via ARM SDK
3. Storage key encrypted with Data Protection API → stored in Cosmos
4. `KubernetesOrchestrator.EnsureWorkspaceNamespaceAsync` — creates Namespace + `azure-storage-creds` Secret + default-deny NetworkPolicy via K8s SDK
5. Record marked Active

**Session Launch** (`POST /api/workspaces/{id}/sessions` → `SessionLauncher.LaunchAsync`):
1. Idempotency check — returns existing Starting/Running session if found
2. DB record created with `Status=Starting`; `DeploymentName` and `ServiceName` are pre-set from the session ID (`sess-<8-hex>` / `svc-<8-hex>`)
3. Returns 202 immediately; `SessionLaunchWorker` runs Helm in background (Channel<T> queue)
4. `helm upgrade --install sess-<id> charts/session -n <namespace> -f <values.json>` — creates K8s Deployment + ClusterIP Service + Ingress
5. Worker writes `AccessUrl` back to DB (`DeploymentName`/`ServiceName` already known)
6. `SessionStatusPoller` (background, every 15 s) calls K8s and transitions `Starting → Running`

**Session Stop** (`DELETE /api/workspaces/{id}/sessions/{sessionId}`):
1. Session marked `Stopping` → published to Service Bus
2. `SessionStopConsumer` runs `helm uninstall <deploymentName> -n <namespace>`
3. Session marked `Stopped`

**Blob Mount Add** (`POST /api/workspaces/{id}/blob-mounts`):
1. Caller provides `storageAccountName`, `resourceGroup`, `containerName` (and optional `mountPath`)
2. Storage key fetched from ARM via `AzureStorageProvisioner.GetStorageKeyAsync` — caller never passes a key
3. K8s Secret `blob-creds-{mountId}` created in workspace namespace
4. PersistentVolume (cluster-scoped) + PersistentVolumeClaim (namespace-scoped) created via `KubernetesOrchestrator.EnsureBlobPvcAsync` using `blob.csi.azure.com` driver with `mountOptions: ["--allow-other"]`
5. Entry saved to Cosmos (`Workspace.BlobMounts`)
6. All currently Running sessions restarted via `LaunchSessionAsync` to pick up the new volume immediately

**Blob Mount Remove** (`DELETE /api/workspaces/{id}/blob-mounts/{mountId}`):
1. PVC and Secret deleted from K8s
2. Entry removed from Cosmos
3. Running sessions restarted

## Key Configuration

`src/SRW.Api/appsettings.json` drives all environment-specific values:
- `Cosmos:Endpoint` — Cosmos DB account endpoint URL
- `Cosmos:AccountKey` — leave empty on AKS to use DefaultAzureCredential (Managed Identity); set for local dev or emulator
- `Azure:SubscriptionId` — Azure subscription for ARM operations
- `Azure:IngressDomain` — base domain for session URLs (`research.example.com` or raw IP)
- `Azure:AllowedSubnetIds` — network allowlist for storage accounts
- `Helm:HelmBinaryPath` — path to `helm` binary (default: `helm` from PATH)
- `Helm:SessionChartPath` — path to `charts/session` chart directory (default: `charts/session`)

In Kubernetes, `Cosmos__Endpoint` and `Cosmos__AccountKey` (or MSI) are injected via a Secret (see `10-api-deployment.yaml`).

## Infrastructure Notes

- **Auth**: Currently reads `X-User-Id` HTTP header (`src/SRW.Api/Auth/CurrentUser.cs`). Keycloak JWT Bearer integration is wired but deferred.
- **Storage keys**: Encrypted at rest using ASP.NET Core Data Protection API before Cosmos storage. Keys are also pushed as K8s Secrets for the CSI driver.
- **Identity**: `DefaultAzureCredential` used throughout — works with both local `az login` and AKS Workload Identity.
- **Health check**: `GET /health` on port 8080 (used by K8s liveness/readiness probes).
- **Session names**: `DeploymentName = sess-<first-8-hex-of-sessionId>`, `ServiceName = svc-<first-8-hex>`. Pre-set in `UserSession.Create` — no output parsing needed after Helm install.
- **Helm release name**: equals `DeploymentName`. `helm list -n <workspace-ns>` shows all running sessions in a workspace.
- **CPU requests**: Default `CpuRequest` is `100m` (limit `2`). Actual RStudio/Jupyter usage is ~150m at peak. Nodes are 2-vCPU (1900m allocatable). If pods stay `Pending` with `Insufficient cpu`, check node allocation: `kubectl describe nodes | grep -A5 "Allocated resources"`. Applications stored in Cosmos before the 100m change may carry `500m` — delete and recreate via `POST /applications` to pick up the new default.
- **AKS node count**: Cluster autoscaler max is 3 nodes. If all 3 nodes are CPU-saturated and autoscaler is in backoff, either stop idle sessions or run `az aks scale --name srw-aks-dev --resource-group srw-dev-rg --node-count 4`.
- **Helm chart location**: `charts/session/` at the repo root. Must be accessible to the API process — for Docker, copy it into the image. For local dev with `dotnet run --project src/SRW.Api`, set `Helm:SessionChartPath` to an absolute path or the path relative to where `dotnet run` executes.
- **Shared file share**: The workspace Azure File Share is mounted at the application's `mountPath` (`/home/jovyan/work` for Jupyter, `/home/rstudio/work` for RStudio) **without** a per-user `subPath`. All users in a workspace see and share the same file share root.
- **secretNamespace**: The CSI driver volume attribute `secretNamespace` is set to the workspace namespace in the Helm chart. Required for cross-namespace secret reads.
- **Jupyter WebSocket (wsUrl)**: JupyterLab 4.x constructs kernel/terminal WebSocket URLs from `wsUrl` in the page config. `--ServerApp.websocket_url=<ingressPath>/` must be passed explicitly — setting only `NotebookApp.base_url` leaves `wsUrl: "/"` and causes kernel disconnections. The `BuildCommandList` method in `KubernetesOrchestrator.cs` always emits all three flags: `ServerApp.base_url`, `ServerApp.websocket_url`, `NotebookApp.base_url`.
- **Application images**: Jupyter uses `gvwkdevacr.azurecr.io/jupyter-notebook-csp:latest` (private ACR). RStudio uses `rocker/rstudio:latest` (public Docker Hub). RStudio runs on port 8787 with auth disabled (`DISABLE_AUTH=true`).

## Blob Container Mounts

Read-only Azure Blob containers (datasets) can be attached to a workspace and automatically appear inside every session's file browser.

**API endpoints:**
```
POST   /api/workspaces/{id}/blob-mounts   — attach a container; restarts Running sessions
GET    /api/workspaces/{id}/blob-mounts   — list attached containers
DELETE /api/workspaces/{id}/blob-mounts/{mountId} — detach; restarts Running sessions
```

**Request body for POST:**
```json
{
  "storageAccountName": "myaccount",
  "resourceGroup": "my-rg",
  "containerName": "my-container",
  "mountPath": "/home/jovyan/work/my-container"   // optional — defaults to /home/jovyan/work/<containerName>
}
```

Multiple containers from the same or different storage accounts can be mounted simultaneously — each gets a unique mount ID.

**K8s resources per mount** (named with mount ID suffix):
- Secret: `blob-creds-{mountId}` — storage account name + key for the CSI driver
- PersistentVolume: `blob-pv-{mountId}` — cluster-scoped, `blob.csi.azure.com`, `ReadOnlyMany`
- PersistentVolumeClaim: `blob-pvc-{mountId}` — namespace-scoped, bound to the PV above

**Critical: `--allow-other` in mountOptions** — blobfuse2 mounts as root (uid=0). Without `--allow-other`, non-root users (jovyan uid=1000, rstudio uid=1000) get `Permission denied`. Must be written as `"--allow-other"` (flag syntax with double dash) in the PV `mountOptions` array — writing `"allow_other"` (no dashes) passes it as a positional arg to blobfuse2 and causes "accepts 1 arg(s), received 2" error. Setting `uid`/`gid`/`allow_other` as `volumeAttributes` on the CSI driver is silently ignored.
