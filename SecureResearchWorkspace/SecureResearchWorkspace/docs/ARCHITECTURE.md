# Secure Research Workspace (SRW) — Architecture Write-up

A multi-tenant research platform on **Azure Kubernetes Service (AKS)**. It provisions isolated
workspaces in which researchers launch Jupyter, RStudio, or custom Docker apps on demand, all
sharing an underlying Azure File Share. This replaces the legacy **TRE model — one App Service
Plan per workspace** — with a single shared, elastic cluster.

Components are flagged **NEW** (introduced by SRW/AKS) vs **EXISTING** (carried forward from the
current TRE world) to make the migration delta explicit.

---

## 1. Design goals

| Goal | How the architecture meets it |
|---|---|
| **Tenant isolation** | One K8s namespace + default-deny NetworkPolicy + dedicated storage account per workspace; one pod per user per app. |
| **Elastic cost** | Pods created on session launch, reaped after idle; cluster autoscaler removes empty nodes. No fixed per-workspace compute. |
| **Concurrent shared data** | Azure File Share (SMB) mounted by every user pod in a workspace, isolated per user via `subPath`. |
| **Responsive API** | Slow Azure/ARM operations run off the HTTP path via Service Bus queues; API returns `202 Accepted` immediately. |
| **Swappable dependencies** | Clean Architecture — core logic depends only on interfaces, so Azure/Keycloak/etc. are single-project swaps. |

---

## 2. Application architecture (Clean Architecture)

Strict dependency direction: **Api → Infrastructure → Core → Domain**

| Layer | Project | Responsibility |
|---|---|---|
| **Domain** | `SRW.Domain` | Pure entities only — `Workspace`, `UserSession`, `WorkspaceApplication`, `WorkspaceUser`. No external dependencies. |
| **Core** | `SRW.Core` | Application services + interface abstractions: `IKubernetesOrchestrator`, `IAzureStorageProvisioner`, `IWorkspaceRepository`, `ISessionRepository`, `IServiceBusPublisher`. |
| **Infrastructure** | `SRW.Infrastructure` | Concrete implementations: Azure SDK (`AzureStorageProvisioner`), Kubernetes client (`KubernetesOrchestrator`), Cosmos persistence (`Repositories`), Service Bus messaging, background jobs. |
| **API** | `SRW.Api` | ASP.NET Core Minimal API endpoints, DI composition root, auth middleware (`CurrentUser` reads `X-User-Id`; Keycloak OIDC wired but deferred). |

Because `Core` talks only to abstractions, the platform can swap Azure for another cloud, or the
dev `X-User-Id` middleware for Keycloak, as a single-project change.

---

## 3. Component inventory (NEW vs EXISTING)

| Component | Role | Status |
|---|---|---|
| **AKS cluster + node pools** | Hosts the API and all session pods | **NEW** |
| **ingress-nginx** | Single ingress entrypoint; path-based routing to session pods | **NEW** |
| **Per-workspace namespace + NetworkPolicy** | Tenant isolation boundary inside the cluster | **NEW** |
| **Azure Service Bus** (3 queues) | Decouples provisioning / stop / cleanup from the HTTP path | **NEW** |
| **Azure Cosmos DB** (NoSQL) | Workspace, session, and encrypted-secret records | EXISTING shared prod infra |
| **Azure Files (per workspace)** | Approved dataset + per-user working storage | **EXISTING** concept, carried forward |
| **App Service Plans** | Legacy per-workspace compute | **being retired** |

> Cosmos DB and Service Bus are shared platform services; Cosmos is excluded from the cost-delta
> model as it already exists in production.

---

## 4. Logical topology

```
Azure Subscription
│
├── Resource Group: srw-platform-rg
│   │
│   ├── AKS Cluster: srw-aks                        ← 1 cluster total
│   │   ├── Namespace: srw-platform                 ← SRW API pod
│   │   ├── Namespace: ingress-nginx                ← nginx ingress controller
│   │   │
│   │   ├── Namespace: ws-alpha-a1b2c3              ← Workspace 1
│   │   │   ├── Secret:        azure-storage-creds  (CSI driver reads this)
│   │   │   ├── NetworkPolicy: default-deny         (ingress only from ingress-nginx)
│   │   │   ├── Deployment/Service/Ingress: sess-…  ← User 1 · Jupyter
│   │   │   └── … one set per user per app
│   │   └── … × 50 workspace namespaces
│   │
│   ├── Storage Account: srw<guid>                  ← Workspace 1
│   │   └── File Share: workspace-share
│   │       ├── user-alice/   ├── user-bob/  …      ← per-user subPath dirs
│   │   … × 50 storage accounts (1 per workspace)
│   │
│   └── (Service Bus + Cosmos referenced below)
│
├── Service Bus Namespace: srw-servicebus           ← 1 namespace
│   ├── Queue: srw-workspace-provision
│   ├── Queue: srw-session-stop
│   └── Queue: srw-workspace-cleanup
│
└── Cosmos DB Account: srw-cosmos                    ← 1 account
    └── Database: srw → containers: workspaces · secrets · sessions
```

The whole platform uses **1 AKS cluster, 1 Cosmos account, 1 Service Bus namespace**. Isolation
between workspaces is enforced by **K8s namespaces + NetworkPolicies + dedicated storage
accounts** — not by separate clusters.

---

## 5. Isolation model

Two layers, both preserved from the workspace concept of today:

- **Workspace = data + membership boundary.** Each workspace owns a member list, an approved
  dataset (its own storage account + File Share), a dedicated namespace, and a default-deny
  NetworkPolicy that permits inbound traffic only from `ingress-nginx`.
- **Per-user, per-app compute boundary (NEW).** Each researcher gets their *own* pod per app
  (own kernel / R session / memory). All pods in a workspace mount the same share, isolated by
  `subPath=<userId>`. Pods are **never** repurposed to another user — they are ephemeral, created
  on launch and deleted on stop. Only the underlying **node** (VM) is shared/bin-packed.

This is stronger isolation than the App Service model, where all 10 users shared one app instance.

---

## 6. Key flows

### 6.1 Workspace provisioning — async (Service Bus)

`POST /api/workspaces`

**On the HTTP path (< 100 ms):** create the domain entity (status `Pending`), seed default apps
(Jupyter, RStudio), join the creator as Admin, save to Cosmos, publish
`WorkspaceProvisionMessage` to `srw-workspace-provision`, return **202 Accepted**.

**Background consumer (`WorkspaceProvisioningConsumer`, off the HTTP path):**
1. Status → `Provisioning`.
2. Provision **Azure Storage Account** (`srw<guid>`, Standard LRS, StorageV2, HTTPS-only, TLS 1.2,
   public access disabled).
3. Create **Azure File Share** (`workspace-share`, SMB, requested quota).
4. Encrypt the storage key (ASP.NET Core Data Protection) → Cosmos `secrets`; push same key as
   K8s Secret `azure-storage-creds` into the workspace namespace.
5. Create **K8s namespace** `ws-<name>-<id>` with management labels.
6. Apply **default-deny NetworkPolicy**.
7. Status → `Active`.

> Failures abandon/requeue the message; after 10 delivery attempts it dead-letters and the
> workspace stays `Failed`.

### 6.2 Session launch — synchronous (HTTP path)

`POST /api/workspaces/{id}/sessions` body `{ applicationId }`

1. Validate workspace is `Active` and app belongs to it.
2. **Idempotency** — return existing `Running`/`Starting` session if present.
3. Create the per-user directory in the share (this becomes the pod's `subPath`).
4. Save session (`Pending`); generate names `sess-<slug>`, `svc-<slug>`, path `/s/<slug>`.
5. Create **K8s Deployment** — one pod, app image, CPU/mem requests+limits, File Share CSI-mounted
   at the user's `subPath`.
6. Create **ClusterIP Service** (port 80).
7. Create **Ingress** rule — `https://<domain>/s/<slug>/(.*)` → service, with WebSocket
   annotations (required by Jupyter).
8. Set `AccessUrl`, status → `Starting`, return to caller.

**Background sync (`SessionStatusPoller`, every 15 s):** reads Deployment ready replicas; when
`ReadyReplicas ≥ 1`, session → `Running` and `StartedAtUtc` is stamped.

### 6.3 Session stop — async (Service Bus)

`DELETE …/sessions/{id}` → status `Stopping`, publish `SessionStopMessage`, return 202.
`SessionStopConsumer` deletes Ingress → Service → Deployment, then status → `Stopped`.

**Idle stop (`IdleSessionReaper`, every 10 min):** sessions idle past the **8h threshold**
(configurable) are queued for stop via the same queue.

### 6.4 Workspace deletion — async (Service Bus)

`DELETE …/workspaces/{id}` → status `Deleting`, publish `WorkspaceCleanupMessage`, return 202.
`WorkspaceCleanupConsumer` deletes the namespace (cascades all pods/services/ingresses/secrets/
policies), deletes the storage account, deletes the Cosmos secret doc.

**Orphan reconciliation (`OrphanResourceCleaner`, every 24 h):** any `srw.io/managed-by=srw-api`
namespace whose workspace is no longer active in Cosmos is removed — self-correcting drift.

---

## 7. Storage & data access

- **Working/shared data:** Azure File Share via **Azure File CSI driver** (`file.csi.azure.com`,
  SMB). Per-user isolation via `subPath=<userId>`; mount options
  `dir_mode=0777,file_mode=0777,uid=1000,gid=1000,mfsymlinks,cache=strict,nosharesock` tuned for
  safe concurrent multi-pod access. A shared drop area can be mounted without `subPath`.
- **datasets in Blob (feasibility):** an additional read-only dataset in a Blob
  container can be mounted as a second volume via the **Azure Blob CSI driver**
  (`blob.csi.azure.com`, blobfuse2 or NFS 3.0) — feasibility confirmed.
- **Session state:** files persist on the share and survive pod restart; in-memory state (live
  kernel / R variables) lives only in the pod and is lost on stop — same behavior as an App
  Service restart today.

---

## 8. Networking & ingress

- **ingress-nginx** is the single entrypoint (internal Azure Load Balancer). It routes
  `https://<domain>/s/<slug>/` to the correct per-session ClusterIP service.
- Each workspace namespace has a **default-deny NetworkPolicy** allowing inbound only from the
  `ingress-nginx` namespace — workspaces cannot reach each other.
- **DNS:** `Azure:IngressDomain` (e.g. `research.example.com`) must resolve to the nginx
  controller IP.

---

## 9. Security

- **Storage keys** encrypted at rest (Data Protection API) before Cosmos storage; also pushed as
  K8s Secrets for the CSI driver. Production target: Key Vault + Workload Identity so keys are
  never handled directly (`IWorkspaceSecretStore` already abstracts this).
- **Identity:** `DefaultAzureCredential` throughout — works with local `az login` and AKS
  Workload Identity.
- **Auth (deferred):** `ICurrentUser` reads `X-User-Id` from a trusted gateway header today;
  Keycloak JWT Bearer is wired but not enabled.
- **Storage accounts:** HTTPS-only, TLS 1.2 minimum, public network access disabled (requires the
  AKS subnet to be allow-listed).

---

## 10. Reliability & self-healing

1. **K8s reconciliation** — Deployments keep the pod running; crashed pods / dead nodes are
   rescheduled automatically.
2. **Liveness/readiness probes** — `GET /health` on port 8080 gates traffic and triggers restart.
3. **Message retry + dead-letter** — failed async operations requeue (up to 10) then dead-letter.
4. **Orphan reconciliation** — drift between Cosmos and the cluster is corrected every 24 h.
5. **Idle reaper** — abandoned sessions are reclaimed automatically.
6. **Cluster autoscaler** — repacks/replaces node capacity as pods come and go.

---

## 11. Scaling & monitoring

- **Scaling:** AKS **Cluster Autoscaler** adds nodes when sessions launch and removes empty ones
  when reaped. The node *pool* scales, not pod replicas (each session is one user's pod).
- **Monitoring (today):** `SessionStatusPoller` reconciles K8s state into session status; jobs log
  provisioning/reaping/cleanup.
- **Monitoring (planned):** **Azure Monitor + Container Insights** to one Log Analytics workspace —
  centralized single-pane-of-glass across all workspaces (labelled `srw.io/workspace-id`), vs 50
  isolated App Service blades today.

---

## 12. Configuration

`src/SRW.Api/appsettings.json` drives environment-specific values:

| Key | Purpose |
|---|---|
| `Cosmos:Endpoint` | Cosmos account endpoint URL |
| `Cosmos:AccountKey` | Empty on AKS → use Managed Identity; set for local/emulator |
| `Azure:AksClusterName` / `Azure:AksResourceGroup` | Cluster targeting |
| `Azure:IngressDomain` | Base domain for session URLs |
| `Azure:AllowedSubnetIds` | Storage account network allowlist |
| `ServiceBus:FullyQualifiedNamespace` | Service Bus namespace |
| `BackgroundJobs:*` | Poll/reaper intervals, idle threshold (8h), concurrency |

In Kubernetes, `Cosmos__Endpoint` / `Cosmos__AccountKey` (or MSI) are injected via a Secret.

---

## 13. Scale reference (50 workspaces × 10 users)

| Resource | All idle | All active (2 apps each) |
|---|---|---|
| AKS Clusters | 1 | 1 |
| K8s Namespaces | 52 (50 + platform + nginx) | 52 |
| Running Pods / Deployments / Services / Ingresses | 0 | 1,000 each |
| K8s Secrets / NetworkPolicies | 50 / 50 | 50 / 50 |
| Azure Storage Accounts / File Shares | 50 / 50 | 50 / 50 |
| Service Bus Namespaces / Queues | 1 / 3 | 1 / 3 |
| Cosmos Accounts (documents) | 1 (~100) | 1 (~1,100) |

First real scaling ceiling: **250 storage accounts per subscription per region** (1 per
workspace). Full detail and node sizing in `RESOURCE_TOPOLOGY.md`.

---

## Related documents
- `RESOURCE_TOPOLOGY.md` — detailed resource counts, flows, and node-sizing guidance.
- `AKS-Migration-QA.md` — pre-read Q&A on the migration (incl. blob-mount feasibility).
- `EXEC_SUMMARY.md` / `COST_ANALYSIS.md` — the cost case (~71% lower compute).
- `README.md` — build/run and code-level walkthrough.
