# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**Secure Research Workspace (SRW)** — a multi-tenant research platform on Azure Kubernetes Service (AKS). It provisions isolated workspaces where researchers can run Jupyter notebooks, RStudio, or custom Docker applications, sharing underlying Azure File Shares over SMB.

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
- **Storage subPath**: Each user's files are isolated under `subPath = sanitize(userId)` on the shared File Share. The Helm chart sets this automatically from the `sanitizedUserId` value.
- **secretNamespace**: The CSI driver volume attribute `secretNamespace` is set to the workspace namespace in the Helm chart. Required for cross-namespace secret reads.
