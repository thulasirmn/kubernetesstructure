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
1. Idempotency check — returns existing active session if found
2. Per-user subdirectory created in File Share
3. K8s Deployment created with `subPath=userId` for storage isolation
4. ClusterIP Service + path-based Ingress rule created
5. Returns URL: `https://research.example.com/s/<slug>/`

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

## Root-Level Loose Files

`AzureStorageProvisioner.cs`, `KubernetesOrchestrator.cs`, `SessionLauncher.cs`, and `WorkspaceProvisioningService.cs` at the repo root are **reference copies** for quick browsing — they are not part of the build. The real sources live under `SecureResearchWorkspace/SecureResearchWorkspace/src/`.
