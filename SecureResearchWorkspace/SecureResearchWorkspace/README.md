# Secure Research Workspace (SRW)

A multi-tenant research platform on Azure Kubernetes Service (AKS). Each **workspace** is an isolation boundary with a dedicated Azure File Share; **researchers** launch Jupyter notebooks, RStudio, or custom Docker images on demand, each getting their own pod and storage sub-directory while sharing the same underlying share concurrently.

---

## Table of Contents

- [Prerequisites](#prerequisites)
- [Local Setup](#local-setup)
- [Project Structure](#project-structure)
- [Key Flows](#key-flows)
- [Background Services](#background-services)
- [Terraform Modules](#terraform-modules)
- [Auth](#auth)
- [Production Checklist](#production-checklist)

---

## Prerequisites

| Tool | Version | Check |
|---|---|---|
| .NET SDK | 8.x | `dotnet --version` |
| Azure CLI | any recent | `az --version` — then `az login` |
| kubectl | any recent | `az aks get-credentials -n srw-aks-dev -g srw-dev-rg` |
| Terraform CLI | >= 1.5 | `terraform version` |
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

Fill in the required values. See **[NEW_MACHINE_SETUP.md](NEW_MACHINE_SETUP.md)** for RBAC and gotchas, and **[NEW_MACHINE_SETUP_TERRAFORM.md](NEW_MACHINE_SETUP_TERRAFORM.md)** for Service Bus + Terraform config keys with a full template.

Key sections:

```json
{
  "Cosmos":      { "Endpoint": "...", "AccountKey": "..." },
  "Azure":       { "SubscriptionId": "...", "AksClusterName": "srw-aks-dev",
                   "AksResourceGroup": "srw-dev-rg", "IngressDomain": "..." },
  "ServiceBus":  { "FullyQualifiedNamespace": "srw-servicebus-dev.servicebus.windows.net",
                   "ConnectionString": "..." },
  "Terraform":   { "TerraformBinaryPath": "terraform",
                   "WorkingRootDir": "C:/temp/srw-terraform",
                   "PluginCacheDir": "C:/temp/terraform-plugins",
                   "StateStorageAccount": "srwterraformstate", ... },
  "BackgroundJobs": { "SessionStatusPollSeconds": 15 }
}
```

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
- **AKS node CPU pressure** — the 3-node dev cluster (2 vCPU each) has ~1900m allocatable per node. Each session requests 100m CPU. If pods go `Pending`, run `kubectl describe pod <name> -n <ns>` — `Insufficient cpu` means too many sessions running. Stop idle sessions or `az aks scale --name srw-aks-dev --resource-group srw-dev-rg --node-count 4`.

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
│   │       ├── SessionLauncher.cs          # Creates session record, enqueues Terraform work
│   │       └── WorkspaceProvisioningService.cs
│   │
│   ├── SRW.Infrastructure/      # Azure SDK + Terraform CLI + Kubernetes client
│   │   ├── Terraform/
│   │   │   ├── TerraformOrchestrator.cs   # Implements both IAzureStorageProvisioner
│   │   │   │                              # and IKubernetesOrchestrator via Terraform CLI
│   │   │   ├── TerraformRunner.cs         # Shells out: init / apply / destroy / output
│   │   │   └── TerraformOptions.cs
│   │   ├── BackgroundJobs/
│   │   │   ├── SessionLaunchWorker.cs     # Consumes Channel<T>, runs terraform apply
│   │   │   ├── SessionStatusPoller.cs     # Polls K8s every 15s, syncs Starting→Running
│   │   │   └── SessionStopConsumer.cs     # Reads Service Bus, runs terraform destroy
│   │   ├── Messaging/                     # ServiceBusPublisher, ServiceBusClientFactory
│   │   └── Persistence/                   # Cosmos DB repositories
│   │
│   └── SRW.Api/                 # Minimal API endpoints, DI composition root
│       ├── Endpoints/
│       │   ├── WorkspaceEndpoints.cs
│       │   ├── SessionEndpoints.cs
│       │   └── ApplicationEndpoints.cs
│       ├── Auth/CurrentUser.cs  # Reads X-User-Id header (Keycloak deferred)
│       └── Program.cs
│
├── terraform/
│   └── modules/
│       ├── workspace-storage/   # Azure Storage Account + File Share
│       ├── workspace-k8s/       # K8s Namespace + azure-storage-creds Secret + NetworkPolicy
│       └── session/             # K8s Deployment + ClusterIP Service + Ingress rule
│
├── k8s/manifests/               # One-time cluster setup (ingress-nginx, RBAC, CSI driver)
├── scripts/                     # setup-azure.sh, setup-azure.ps1
├── docs/                        # Architecture docs, Word implementation plan
├── NEW_MACHINE_SETUP.md         # Local dev setup, RBAC, gotchas
└── NEW_MACHINE_SETUP_TERRAFORM.md  # Service Bus + Terraform config keys + full appsettings template
```

**Dependency direction:** `Api → Infrastructure → Core → Domain`. Core talks only to abstractions — swapping Azure for another cloud or Keycloak for another IdP is a single-project change.

---

## Key Flows

### Workspace Provisioning (`POST /api/workspaces`)

```
POST /api/workspaces
  │
  ▼
WorkspaceProvisioningService.CreateAsync
  ├── 1. Insert Workspace (Pending) in Cosmos
  ├── 2. TerraformOrchestrator.ProvisionAsync
  │       → terraform apply workspace-storage module
  │       → Creates srw<guid> storage account + "workspace-share" file share
  │       → Returns storage account name + primary key via terraform output
  ├── 3. Encrypt storage key with Data Protection API → store in Cosmos
  ├── 4. TerraformOrchestrator.EnsureWorkspaceNamespaceAsync
  │       → terraform apply workspace-k8s module
  │       → Creates K8s namespace ws-<name>-<id>
  │       → Creates Secret "azure-storage-creds" (CSI driver reads this to mount the share)
  │       → Creates default-deny NetworkPolicy (only ingress-nginx allowed in)
  └── 5. Mark Workspace Active
```

### Session Launch (`POST /api/workspaces/{id}/sessions`)

The HTTP response returns **202 immediately**. Terraform runs in the background.

```
POST /api/workspaces/{id}/sessions    body: { "applicationId": "<guid>" }
  │
  ▼
SessionLauncher.LaunchAsync
  ├── 1. Validate workspace (must be Active) + application
  ├── 2. Idempotency: return existing session if status is Starting or Running
  ├── 3. Create UserSession (Status=Starting, DeploymentName="") → Cosmos
  └── 4. Enqueue to Channel<T> → return 202

  [background — SessionLaunchWorker]
  ├── 5. terraform init + apply   (session module)
  │       → Creates K8s Deployment "sess-<8-char-id>"  (wait_for_rollout=false)
  │       → Creates ClusterIP Service
  │       → Creates Ingress rule  /s/<slug>(/|$)(.*) → service:80
  ├── 6. terraform output → reads deployment_name, service_name, access_url
  ├── 7. Write DeploymentName + ServiceName + AccessUrl back to Cosmos
  │
  [background — SessionStatusPoller, every 15 s]
  └── 8. K8s reports ReadyReplicas >= 1 → update Status=Running in Cosmos
```

Poll `GET /api/workspaces/{id}/sessions/{sessionId}` to observe status transitions.

### Session Stop (`DELETE /api/workspaces/{id}/sessions/{sessionId}`)

```
DELETE /api/workspaces/{id}/sessions/{sessionId}
  ├── Mark session Stopping → Cosmos
  └── Publish SessionStopMessage to Service Bus  (202 returned immediately)

  [background — SessionStopConsumer]
  ├── terraform destroy  (session module)
  │     → Deletes K8s Deployment + Service + Ingress
  └── Mark session Stopped + set StoppedAtUtc → Cosmos
```

### Storage Isolation Per User

The Azure File CSI driver mounts the workspace File Share with `subPath = sanitize(userId)` — each researcher's files live under their own sub-directory within the shared share. Their pod can only read and write their own directory. A `/shared` mount without subPath can be added as a second volume for collaboration.

---

## Background Services

| Service | Trigger | What it does |
|---|---|---|
| `SessionLaunchWorker` | In-process `Channel<T>` | Runs `terraform apply` for each queued session; writes DeploymentName/AccessUrl back to Cosmos |
| `SessionStatusPoller` | Timer (15 s) | Reads all Starting/Running sessions from Cosmos; calls K8s for pod readiness; syncs status |
| `SessionStopConsumer` | Azure Service Bus | Runs `terraform destroy` for stop-requested sessions; marks Stopped in Cosmos |

---

## Terraform Modules

State is stored remotely in Azure Blob (`srwterraformstate` storage account, container `srw-tf-state`).

| Module | State key | Resources |
|---|---|---|
| `workspace-storage` | `workspaces/<account>-storage.tfstate` | Azure Storage Account, File Share |
| `workspace-k8s` | `workspaces/<namespace>-k8s.tfstate` | K8s Namespace, `azure-storage-creds` Secret, NetworkPolicy |
| `session` | `sessions/<sessionId>.tfstate` | K8s Deployment, ClusterIP Service, Ingress |

The session module uses `wait_for_rollout = false` on the Deployment resource. **Do not remove this.** Pod readiness is owned by `SessionStatusPoller`; having Terraform wait on it causes false `Failed` status when nodes are under pressure.

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
