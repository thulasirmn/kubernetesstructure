# Secure Research Workspace (SRW)

A multi-tenant research platform on Azure Kubernetes Service (AKS). Each **workspace** is an isolation boundary with a dedicated Azure File Share; **researchers** launch Jupyter notebooks, RStudio, or custom Docker images on demand, each getting their own pod and storage sub-directory while sharing the same underlying share concurrently.

---

## Table of Contents

- [Prerequisites](#prerequisites)
- [Local Setup](#local-setup)
- [Project Structure](#project-structure)
- [Key Flows](#key-flows)
- [Background Services](#background-services)
- [Auth](#auth)
- [Production Checklist](#production-checklist)

---

## Prerequisites

| Tool | Version | Check |
|---|---|---|
| .NET SDK | 8.x | `dotnet --version` |
| Azure CLI | any recent | `az --version` — then `az login` |
| kubectl | any recent | `az aks get-credentials -n srw-aks-dev -g srw-dev-rg` |
| Helm CLI | >= 3.x | `helm version` |
| HTTPS dev cert | — | `dotnet dev-certs https --trust` |

The API uses `DefaultAzureCredential` throughout — `az login` is sufficient for local dev (no service principal needed).

---

## Local Setup

### 1. Clone and build

```powershell
dotnet restore
dotnet build
```

### 2. Create `appsettings.Development.json`

This file is **gitignored** (contains live secrets). Copy the template:

```powershell
Copy-Item src\SRW.Api\appsettings.Development.json.example `
          src\SRW.Api\appsettings.Development.json
```

Fill in the required values. See **[NEW_MACHINE_SETUP.md](NEW_MACHINE_SETUP.md)** for RBAC and gotchas, and **[NEW_MACHINE_SETUP_HELM.md](NEW_MACHINE_SETUP_HELM.md)** for the full appsettings template with all sections.

Key sections:

```json
{
  "Cosmos":     { "Endpoint": "...", "AccountKey": "..." },
  "Azure":      { "SubscriptionId": "...", "AksClusterName": "srw-aks-dev",
                  "AksResourceGroup": "srw-dev-rg", "IngressDomain": "..." },
  "ServiceBus": { "FullyQualifiedNamespace": "srw-servicebus-dev.servicebus.windows.net",
                  "ConnectionString": "..." },
  "Helm":       { "HelmBinaryPath": "helm",
                  "SessionChartPath": "C:/absolute/path/to/charts/session" },
  "BackgroundJobs": { "SessionStatusPollSeconds": 15 }
}
```

> **`Helm:SessionChartPath`** — when running `dotnet run --project src/SRW.Api`, the working directory is `src/SRW.Api`, so the default relative path `charts/session` won't resolve. Set this to the **absolute** path of the `charts/session` directory in your local clone.

> No database migrations needed — Cosmos containers are created automatically at startup via `CosmosContainerProvider.InitializeAsync()`.

### 3. Run

```powershell
dotnet run --project src/SRW.Api
```

- Health: `GET http://localhost:<port>/health` → `{ "status": "ok" }`
- Swagger (Development only): `https://localhost:<port>/swagger`

### 4. Open a session in the browser

The ingress LoadBalancer IP is typically unreachable from a dev machine. Port-forward instead:

```powershell
kubectl port-forward -n ingress-nginx svc/ingress-nginx-controller 80:80
```

Then open `http://localhost/s/<slug>/` using the `accessUrl` slug returned by `GET /sessions`.

### Common gotchas

- **Data Protection key ring** — storage keys are encrypted with ASP.NET Core Data Protection. Keys live in `%LOCALAPPDATA%\ASP.NET\DataProtection-Keys` by default. If you point a second machine at the same Cosmos DB, it cannot decrypt storage keys written by the first machine. To share, persist the key ring to Azure Blob + Key Vault.
- **Port** — `src/SRW.Api/Properties/launchSettings.json` is untracked in git. On a new machine the port may differ or default to 5000/5001 unless you copy that file.
- **`Helm:SessionChartPath`** — must be an absolute path in `appsettings.Development.json`. The chart directory (`charts/session/`) lives at the repo root, not under `src/SRW.Api`.
- **AKS node CPU pressure** — the 3-node dev cluster (2 vCPU each) has ~1900m allocatable per node. Each session requests 100m CPU. If pods go `Pending`, run `kubectl describe pod <name> -n <ns>` — `Insufficient cpu` means too many sessions running. Stop idle sessions or `az aks scale --name srw-aks-dev --resource-group srw-dev-rg --node-count 4`.
- **`resourceGroup` is required** when calling `POST /api/workspaces` — pass the Azure resource group where the workspace storage account should be created (e.g. `srw-dev-rg`).

---

## Project Structure

```
SecureResearchWorkspace.sln
├── src/
│   ├── SRW.Domain/              # Entities only — no external dependencies
│   │   └── Entities/
│   │       ├── Workspace.cs
│   │       ├── UserSession.cs   # Status: Pending→Starting→Running→Stopping→Stopped/Failed
│   │       ├── WorkspaceApplication.cs
│   │       └── WorkspaceUser.cs
│   │
│   ├── SRW.Core/                # Application services + interface abstractions (no Azure SDK)
│   │   ├── Abstractions/        # IKubernetesOrchestrator, IAzureStorageProvisioner,
│   │   │                        # IWorkspaceRepository, ISessionRepository, ISessionProvisioningQueue
│   │   └── Services/
│   │       ├── SessionLauncher.cs          # Creates session record, enqueues to background worker
│   │       └── WorkspaceProvisioningService.cs
│   │
│   ├── SRW.Infrastructure/      # Azure ARM SDK + Kubernetes client SDK + Helm CLI
│   │   ├── Azure/
│   │   │   └── AzureStorageProvisioner.cs  # ARM SDK: creates Storage Account + File Share
│   │   ├── Kubernetes/
│   │   │   └── KubernetesOrchestrator.cs   # K8s SDK: Namespace/Secret/NetworkPolicy + Helm for sessions
│   │   ├── Helm/
│   │   │   ├── HelmRunner.cs               # Shells out: helm upgrade --install / helm uninstall
│   │   │   └── HelmOptions.cs              # HelmBinaryPath, SessionChartPath
│   │   ├── BackgroundJobs/
│   │   │   ├── SessionLaunchWorker.cs      # Consumes Channel<T>, runs Helm install
│   │   │   ├── SessionStatusPoller.cs      # Polls K8s every 15s, syncs Starting→Running
│   │   │   └── SessionStopConsumer.cs      # Reads Service Bus, runs Helm uninstall
│   │   ├── Messaging/                      # ServiceBusPublisher, ServiceBusClientFactory
│   │   └── Persistence/                    # Cosmos DB repositories
│   │
│   └── SRW.Api/                 # Minimal API endpoints, DI composition root
│       ├── Endpoints/
│       │   ├── WorkspaceEndpoints.cs
│       │   ├── SessionEndpoints.cs
│       │   └── ApplicationEndpoints.cs
│       ├── Auth/CurrentUser.cs  # Reads X-User-Id header (Keycloak deferred)
│       └── Program.cs
│
├── charts/
│   └── session/                 # Helm chart for per-session K8s resources
│       ├── Chart.yaml
│       ├── values.yaml
│       └── templates/
│           ├── deployment.yaml  # Pod with Azure File CSI volume mount
│           ├── service.yaml     # ClusterIP service
│           └── ingress.yaml     # /s/<slug> ingress rule
│
├── k8s/manifests/               # One-time cluster setup (ingress-nginx, RBAC, CSI driver)
├── scripts/                     # setup-azure.sh, setup-azure.ps1
├── docs/                        # Architecture docs, ADO story templates
├── NEW_MACHINE_SETUP.md         # Local dev setup, RBAC, gotchas
└── NEW_MACHINE_SETUP_HELM.md    # Helm + Service Bus config keys + full appsettings template
```

**Dependency direction:** `Api → Infrastructure → Core → Domain`. Core talks only to abstractions — swapping Azure for another cloud or Keycloak for another IdP is a single-project change.

---

## Key Flows

### Workspace Provisioning (`POST /api/workspaces`)

Returns **202 Accepted** immediately. Provisioning runs in `WorkspaceProvisioningConsumer`.

```
POST /api/workspaces   body: { "name": "...", "resourceGroup": "srw-dev-rg", "quotaGiB": 100 }
  │
  ▼
WorkspaceProvisioningService.ProvisionAsync  (background)
  ├── 1. Mark Workspace Provisioning in Cosmos
  ├── 2. AzureStorageProvisioner.ProvisionAsync  (Azure ARM SDK)
  │       → Creates srw<guid> Storage Account in the given resource group
  │       → Creates "workspace-share" File Share
  │       → Returns primary storage account key
  ├── 3. Encrypt storage key with Data Protection API → store in Cosmos
  ├── 4. KubernetesOrchestrator.EnsureWorkspaceNamespaceAsync  (K8s client SDK)
  │       → Creates Namespace ws-<name>-<id> with srw.io labels
  │       → Creates Secret "azure-storage-creds" (CSI driver reads this to mount the share)
  │       → Creates default-deny NetworkPolicy (only ingress-nginx allowed in)
  └── 5. Mark Workspace Active
```

### Session Launch (`POST /api/workspaces/{id}/sessions`)

Returns **202 Accepted** immediately. Helm runs in the background.

```
POST /api/workspaces/{id}/sessions   body: { "applicationId": "<guid>" }
  │
  ▼
SessionLauncher.LaunchAsync
  ├── 1. Validate workspace (must be Active) + application
  ├── 2. Idempotency: return existing session if status is Starting or Running
  ├── 3. Create UserSession (Status=Starting)
  │       DeploymentName = sess-<first-8-hex-of-sessionId>
  │       ServiceName    = svc-<first-8-hex-of-sessionId>
  │       IngressPath    = /s/<first-10-hex-of-sessionId>
  └── 4. Enqueue to Channel<T> → return 202

  [background — SessionLaunchWorker]
  ├── 5. helm upgrade --install sess-<id> charts/session -n <namespace> -f <values.json>
  │       → Creates K8s Deployment (one pod per session)
  │       → Creates ClusterIP Service
  │       → Creates Ingress rule  /s/<slug>(/|$)(.*) → service:80
  └── 6. Write AccessUrl back to Cosmos  (DeploymentName/ServiceName already set at step 3)

  [background — SessionStatusPoller, every 15 s]
  └── 7. K8s reports ReadyReplicas >= 1 → update Status=Running in Cosmos
```

Poll `GET /api/workspaces/{id}/sessions/{sessionId}` to observe status transitions.

### Session Stop (`DELETE /api/workspaces/{id}/sessions/{sessionId}`)

```
DELETE /api/workspaces/{id}/sessions/{sessionId}
  ├── Mark session Stopping → Cosmos
  └── Publish SessionStopMessage to Service Bus  (202 returned immediately)

  [background — SessionStopConsumer]
  ├── helm uninstall sess-<id> -n <namespace>
  │     → Deletes K8s Deployment + Service + Ingress
  └── Mark session Stopped + set StoppedAtUtc → Cosmos
```

### Storage Isolation Per User

The Azure File CSI driver mounts the workspace File Share with `subPath = sanitize(userId)` — each researcher's files live under their own sub-directory within the shared share. Their pod can only read and write their own directory.

---

## Background Services

| Service | Trigger | What it does |
|---|---|---|
| `WorkspaceProvisioningConsumer` | Azure Service Bus | Runs ARM SDK + K8s SDK to provision storage + namespace; marks workspace Active or Failed |
| `SessionLaunchWorker` | In-process `Channel<T>` | Runs `helm upgrade --install` for each queued session; writes AccessUrl back to Cosmos |
| `SessionStatusPoller` | Timer (15 s) | Reads all Starting/Running sessions; calls K8s for pod readiness; syncs status to Running |
| `SessionStopConsumer` | Azure Service Bus | Runs `helm uninstall` for stop-requested sessions; marks session Stopped in Cosmos |

---

## Auth

`ICurrentUser` is the boundary. The dev implementation reads the `X-User-Id` (and optional `X-User-Name`) HTTP header in `src/SRW.Api/Auth/CurrentUser.cs`. The API trusts whatever value you send — no real auth in dev.

To wire Keycloak JWT Bearer:

```csharp
// Program.cs
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o => {
        o.Authority = "https://keycloak.example.com/realms/research";
        o.Audience  = "srw-api";
    });
// Populate ICurrentUser from ClaimsPrincipal in a replacement middleware.
// No other code changes needed — all services consume ICurrentUser.
```

---

## Production Checklist

- [ ] Storage accounts: enable **Private Endpoint** + disable public network access (`Azure:AllowedSubnetIds`)
- [ ] **Data Protection key ring** → persist to Azure Blob + Key Vault so all API replicas share the same encryption keys
- [ ] Per-workspace storage key → **Azure Key Vault** instead of encrypted Cosmos column (`IWorkspaceSecretStore` is already abstracted)
- [ ] AKS **Workload Identity** for the `srw-api` pod → federated Managed Identity with Storage Account Contributor; removes all key handling
- [ ] Add **resource quotas** + LimitRanges per workspace namespace
- [ ] Add **PodSecurity** standards (`restricted` profile) on workspace namespaces
- [ ] Replace dev auth middleware with **Keycloak OIDC**
- [ ] Restrict ingress to corporate IPs via `nginx.ingress.kubernetes.io/whitelist-source-range`
- [ ] Wire **Azure Monitor** + Container Insights; alert on storage throttling and pod pending duration
- [ ] Idle-session reaper (`BackgroundJobs:IdleReaperIntervalMinutes` + `IdleSessionThresholdHours` config already wired)
- [ ] Copy `charts/session/` into the Docker image at build time (required for AKS deployment — `Helm:SessionChartPath` must point inside the container image)
