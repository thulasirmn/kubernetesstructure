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

**Running on a new/local machine:** see [`NEW_MACHINE_SETUP.md`](NEW_MACHINE_SETUP.md) — tooling, `appsettings.Development.json` config keys, Azure RBAC, the Data Protection key-ring gotcha, and the ingress port-forward for opening sessions locally.

## Architecture

Clean Architecture with strict dependency direction: **Api → Infrastructure → Core → Domain**

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `SRW.Domain` | Entities only (`Workspace`, `UserSession`, `WorkspaceApplication`, `WorkspaceUser`). No external dependencies. |
| Core | `SRW.Core` | Application services and interface abstractions (`IKubernetesOrchestrator`, `IAzureStorageProvisioner`, `IWorkspaceRepository`, `ISessionRepository`). |
| Infrastructure | `SRW.Infrastructure` | Implements interfaces: Azure SDK calls (`AzureStorageProvisioner`), Kubernetes client (`KubernetesOrchestrator`), EF Core (`SrwDbContext`, `Repositories`). |
| API | `SRW.Api` | ASP.NET Core Minimal API endpoints, DI composition root, auth middleware (`CurrentUser` reads `X-User-Id` header — Keycloak OIDC is deferred). |

## Key Flows

**Workspace Provisioning** (`POST /api/workspaces` → `WorkspaceProvisioningService.CreateAsync`):
1. DB record created (Pending)
2. Azure Storage Account provisioned (`srw<guid>`)
3. File Share created (`workspace-share`)
4. Storage key stored encrypted in DB and as a K8s Secret
5. K8s Namespace created (`ws-<name>-<id>`) with default-deny `NetworkPolicy`
6. Record marked Active

**Session Launch** (`POST /api/workspaces/{id}/sessions` → `SessionLauncher.LaunchAsync`):
1. Idempotency check — returns existing Starting/Running session if found
2. DB record created with `Status=Starting`, `DeploymentName=""` (Terraform is source of truth)
3. Returns 202 immediately; `SessionLaunchWorker` runs Terraform in background (Channel<T> queue)
4. Terraform creates K8s Deployment + ClusterIP Service + Ingress rule (`wait_for_rollout=false` — do NOT remove this; pod readiness is handled by the poller, not Terraform)
5. Worker writes `DeploymentName`, `ServiceName`, `AccessUrl` back to DB after `terraform apply`
6. `SessionStatusPoller` (background, every 15 s) calls K8s and transitions `Starting → Running`

## Key Configuration

`src/SRW.Api/appsettings.json` drives all environment-specific values:
- `Cosmos:Endpoint` — Cosmos DB account endpoint URL
- `Cosmos:AccountKey` — leave empty on AKS to use DefaultAzureCredential (Managed Identity); set for local dev or emulator
- `Azure:AksClusterName` / `Azure:AksResourceGroup` — cluster targeting
- `Azure:IngressDomain` — base domain for session URLs (`research.example.com`)
- `Azure:AllowedSubnetIds` — network allowlist for storage accounts

In Kubernetes, `Cosmos__Endpoint` and `Cosmos__AccountKey` (or MSI) are injected via a Secret (see `10-api-deployment.yaml`).

## Infrastructure Notes

- **Auth**: Currently reads `X-User-Id` HTTP header (`src/SRW.Api/Auth/CurrentUser.cs`). Keycloak JWT Bearer integration is wired but deferred.
- **Storage keys**: Encrypted at rest using ASP.NET Core Data Protection API before SQL storage. Keys are also pushed as K8s Secrets for the CSI driver.
- **Identity**: `DefaultAzureCredential` used throughout — works with both local `az login` and AKS Workload Identity (the pod annotation for Workload Identity is present but not fully wired).
- **Health check**: `GET /health` on port 8080 (used by K8s liveness/readiness probes).
- **Terraform rollout wait**: `wait_for_rollout = false` is set on `kubernetes_deployment` in `terraform/modules/session/main.tf`. Do NOT change this — Terraform must not block on pod readiness; `SessionStatusPoller` owns that transition. Removing it causes sessions to go `Failed` whenever a pod is slow to schedule (e.g. node pressure).
- **CPU requests**: Default `CpuRequest` is `100m` (limit `2`). Actual RStudio/Jupyter usage is ~150m at peak. Nodes are 2-vCPU (1900m allocatable). If pods stay `Pending` with `Insufficient cpu`, check node allocation: `kubectl describe nodes | grep -A5 "Allocated resources"`. Existing applications stored in Cosmos before the 100m change still carry `500m` — delete and recreate via `POST /applications` to pick up the new default.
- **AKS node count**: Cluster autoscaler max is 3 nodes. If all 3 nodes are CPU-saturated and autoscaler is in backoff, either stop idle sessions or run `az aks scale --name srw-aks-dev --resource-group srw-dev-rg --node-count 4`.
- **Terraform destroy / identity error**: The Kubernetes provider can throw `Unexpected Identity Change` on destroy when K8s controllers modified the resource after Terraform created it. Workaround: kubectl delete the resources manually, then retry the `DELETE /sessions/{id}` endpoint — Service Bus will re-deliver and destroy succeeds against an empty cluster.

## Root-Level Loose Files

`AzureStorageProvisioner.cs`, `KubernetesOrchestrator.cs`, `SessionLauncher.cs`, and `WorkspaceProvisioningService.cs` at the repo root are **reference copies** for quick browsing — they are not part of the build. The real sources live under `SecureResearchWorkspace/SecureResearchWorkspace/src/`.
