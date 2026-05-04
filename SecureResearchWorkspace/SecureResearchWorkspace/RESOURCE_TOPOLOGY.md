# Resource Topology: 50 Workspaces × 10 Users

This document describes the Azure and Kubernetes resources provisioned by the Secure Research Workspace (SRW) platform at scale, and how they relate to each other.

---

## Workspace Creation Flow (Async — Service Bus Triggered)

**Trigger:** `POST /api/workspaces`

```json
{
  "name": "my-research",
  "description": "...",
  "resourceGroup": "srw-platform-rg",
  "quotaGiB": 100
}
```

**API request (synchronous — completes in < 100 ms):**

1. **Domain entity created** — generates unique IDs for the workspace, a storage account name (`srw<18-char-guid>`), file share name (`workspace-share`), and K8s namespace (`ws-<name>-<6-char-id>`). Status = `Pending`.
2. **Default applications seeded** — Jupyter Notebook and RStudio Server added to the workspace's embedded app list.
3. **Creator joined as Admin** — the calling user is embedded in the workspace document.
4. **Saved to Cosmos DB** — workspace record written to the `workspaces` container. Status = `Pending`.
5. **Message published to Service Bus** — `WorkspaceProvisionMessage { WorkspaceId }` sent to the `srw-workspace-provision` queue.
6. **HTTP 202 Accepted returned** — caller gets the workspace record immediately with status `Pending`.

**Background consumer (`WorkspaceProvisioningConsumer`) — runs off the HTTP path:**

7. **Message consumed** — up to 5 messages processed concurrently; 10-minute auto-lock renewal covers long ARM operations.
8. **Status → `Provisioning`** — updated in Cosmos via `workspace.MarkProvisioning()`.
9. **Azure Storage Account provisioned** — ARM API creates a new Storage Account in the given resource group (Standard LRS, StorageV2, HTTPS-only, TLS 1.2 minimum, public network access disabled).
10. **Azure File Share created** — inside that storage account (`workspace-share`, SMB protocol, requested quota in GiB).
11. **Storage account key stored** — primary key encrypted via ASP.NET Core Data Protection and saved to the Cosmos `secrets` container. Same key pushed as a Kubernetes `Secret` named `azure-storage-creds` in the workspace namespace (for the CSI driver).
12. **Kubernetes Namespace created** — `ws-<name>-<id>` with labels `srw.io/workspace-id` and `srw.io/managed-by=srw-api`.
13. **NetworkPolicy applied** — `default-deny` ingress policy, allowing inbound only from pods in the `ingress-nginx` namespace.
14. **Status → `Active`** — updated in Cosmos. Workspace is ready.

> If any step in the consumer fails, the message is abandoned and requeued. After 10 delivery attempts (Service Bus dead-letter threshold), the message is moved to the dead-letter queue for investigation. The workspace remains in `Failed` status.

---

## Session (Application) Launch Flow

**Trigger:** `POST /api/workspaces/{workspaceId}/sessions`

```json
{ "applicationId": "<guid-of-jupyter-or-rstudio>" }
```

**Steps executed in order (synchronous — runs on the HTTP path):**

1. **Workspace validated** — must be `Active`, otherwise rejected.
2. **Application validated** — fetched from workspace's embedded app list; must belong to the same workspace.
3. **Idempotency check** — if the user already has a `Running` or `Starting` session for this app, the existing session is returned without creating anything new.
4. **User directory created** — Azure Files data-plane API creates a subdirectory named after the user inside the workspace's `workspace-share`. This becomes the pod's `subPath` mount (isolated per user, shared file system per workspace).
5. **Session record saved** — Cosmos `sessions` container, status = `Pending`. Names generated: `sess-<10char>` (deployment), `svc-<10char>` (service), path `/s/<10char>` (ingress).
6. **Kubernetes Deployment created** — one pod with the application's container image, CPU/memory requests and limits, and the file share CSI-mounted at the user's `subPath`.
7. **Kubernetes Service created** — `ClusterIP` type on port 80, selecting the session's pod.
8. **Kubernetes Ingress rule created** — path-based routing: `https://<ingressDomain>/s/<slug>/(.*)` → ClusterIP service. Nginx annotations for WebSocket support (required by Jupyter).
9. **Session updated** — `AccessUrl` set to `https://research.example.com/s/<slug>/`, status = `Starting`. Returned to the caller.

**Background status sync (`SessionStatusPoller` — every 15 seconds):**

10. **K8s queried** — polls all `Starting`/`Running` sessions; reads Deployment ready replicas.
11. **Status updated** — if K8s says `ReadyReplicas >= 1`, session moves to `Running`; `StartedAtUtc` is stamped.

---

## Session Stop Flow (Async — Service Bus Triggered)

**Trigger:** `DELETE /api/workspaces/{workspaceId}/sessions/{sessionId}`

1. **Status → `Stopping`** — updated in Cosmos immediately.
2. **Message published** — `SessionStopMessage { SessionId, WorkspaceId }` sent to `srw-session-stop` queue.
3. **HTTP 202 Accepted returned**.

**Background consumer (`SessionStopConsumer`):**

4. **Kubernetes resources deleted** — Ingress, Service, Deployment removed in order.
5. **Status → `Stopped`**, `StoppedAtUtc` stamped, Cosmos updated.

**Automatic idle stop (`IdleSessionReaper` — every 10 minutes):**

- Sessions with no activity for > 8 hours (configurable) are automatically queued for stopping via the same `srw-session-stop` queue.

---

## Workspace Deletion Flow (Async — Service Bus Triggered)

**Trigger:** `DELETE /api/workspaces/{id}`

1. **Status → `Deleting`** — updated in Cosmos immediately.
2. **Message published** — `WorkspaceCleanupMessage { WorkspaceId }` sent to `srw-workspace-cleanup` queue.
3. **HTTP 202 Accepted returned**.

**Background consumer (`WorkspaceCleanupConsumer`):**

4. **Kubernetes namespace deleted** — cascades to all Deployments, Services, Ingresses, Secrets, and NetworkPolicies.
5. **Azure Storage Account deleted** — cascades to all File Shares and user directories.
6. **Cosmos secret document deleted**.
7. **Workspace status → `Deleting`** in Cosmos (final state — document retained for audit; can be hard-deleted separately).

**Orphan reconciliation (`OrphanResourceCleaner` — every 24 hours):**

- Lists all K8s namespaces with label `srw.io/managed-by=srw-api`.
- Any namespace whose workspace is no longer `Active`/`Provisioning`/`Pending` in Cosmos is deleted automatically.

---

## Azure Resources to Pre-Create (One-Time Setup)

These must exist **before** the API starts processing requests.

### 1. Resource Group
- Name: `srw-platform-rg` (matches `appsettings.json → Azure:AksResourceGroup`)
- All workspace storage accounts are provisioned here (or you can pass a different `resourceGroup` per workspace in the API request).

### 2. AKS Cluster
- Name: `srw-aks` (matches `Azure:AksClusterName`)
- Enable the Azure File CSI driver:
  ```bash
  az aks update -n srw-aks -g srw-platform-rg --enable-file-driver
  ```
- Apply the cluster RBAC setup:
  ```bash
  kubectl apply -f k8s/manifests/00-cluster-setup.yaml
  ```
  This creates the `srw-platform` namespace, `srw-api` ServiceAccount, and the ClusterRole that gives the API permission to manage namespaces, deployments, services, ingresses, and network policies.

### 3. ingress-nginx (Helm install on AKS)
```bash
helm upgrade --install ingress-nginx ingress-nginx/ingress-nginx \
  --namespace ingress-nginx --create-namespace \
  --set controller.service.annotations."service\.beta\.kubernetes\.io/azure-load-balancer-internal"=true
```
The workspace `NetworkPolicy` allows ingress only from the `ingress-nginx` namespace. The label `name: ingress-nginx` on that namespace is applied by `00-cluster-setup.yaml`.

### 4. Azure Cosmos DB (NoSQL) Account
- API: NoSQL (Core SQL)
- The database (`srw`) and containers (`workspaces`, `sessions`, `secrets`) are **created automatically at startup** — no manual setup needed.
- Copy the endpoint URI to `appsettings.json → Cosmos:Endpoint`.
- For local dev: set `Cosmos:AccountKey`. On AKS: leave blank and use Managed Identity.

### 5. Azure Service Bus Namespace
- Tier: **Standard** or **Premium** (Standard is sufficient for this workload).
- Name: `srw-servicebus` (maps to `ServiceBus:FullyQualifiedNamespace` in appsettings).
- Create the following **queues** inside the namespace (default settings, dead-letter enabled):

| Queue Name | Consumer | Max Delivery Count |
|---|---|---|
| `srw-workspace-provision` | `WorkspaceProvisioningConsumer` | 10 |
| `srw-session-stop` | `SessionStopConsumer` | 10 |
| `srw-workspace-cleanup` | `WorkspaceCleanupConsumer` | 10 |

```bash
az servicebus namespace create \
  --name srw-servicebus \
  --resource-group srw-platform-rg \
  --sku Standard

az servicebus queue create --name srw-workspace-provision \
  --namespace-name srw-servicebus --resource-group srw-platform-rg \
  --max-delivery-count 10

az servicebus queue create --name srw-session-stop \
  --namespace-name srw-servicebus --resource-group srw-platform-rg \
  --max-delivery-count 10

az servicebus queue create --name srw-workspace-cleanup \
  --namespace-name srw-servicebus --resource-group srw-platform-rg \
  --max-delivery-count 10
```

### 6. Managed Identity / RBAC for the API Pod (Production)
The API uses `DefaultAzureCredential` for all Azure calls. The pod's managed identity needs:

| Resource | Required Role |
|---|---|
| Resource Group (workspace storage accounts) | `Contributor` or `Storage Account Contributor` |
| Cosmos DB account | `Cosmos DB Built-in Data Contributor` |
| Service Bus namespace | `Azure Service Bus Data Owner` |

### 7. DNS / Ingress Domain
The domain in `Azure:IngressDomain` (e.g. `research.example.com`) must resolve to the nginx ingress controller's IP. Configure via Azure DNS or your DNS provider after AKS + nginx are deployed.

---

## Resource Breakdown: 50 Workspaces × 10 Users

### Azure Storage

| Resource | Count | Notes |
|---|---|---|
| Storage Accounts | **50** | 1 per workspace, name = `srw<18-char-guid>` |
| Azure File Shares | **50** | 1 per workspace (`workspace-share`) |
| Directories inside each share | **10** | 1 per user, created on first session launch |

**Total: 50 storage accounts · 50 shares · 500 user directories**

> Azure default limit is **250 storage accounts per subscription per region**. 50 is well within this. Request a quota increase if you plan to scale beyond ~200 workspaces.

---

### Azure Service Bus

| Resource | Count | Notes |
|---|---|---|
| Namespace | **1** | Shared across the whole platform |
| Queues | **3** | provision, session-stop, workspace-cleanup |
| Messages (peak) | ~**500–1,000/day** | Spiky — provisioning + stop events |

---

### Cosmos DB

| Container | Documents | Partition Key |
|---|---|---|
| `workspaces` | **50** | `/id` — each doc embeds its 10 users + 2 applications |
| `secrets` | **50** | `/workspaceId` — one encrypted storage key per workspace |
| `sessions` | **0 → up to 1,000** | `/workspaceId` — 1 doc per active/past session |

**One Cosmos DB account, one database (`srw`), three containers.**

---

### Kubernetes (1 AKS Cluster)

#### Fixed resources — created once per workspace, stay alive

| K8s Resource | Count | Name Pattern |
|---|---|---|
| Namespaces | **50** | `ws-<name>-<6-char-id>` |
| Secrets | **50** | `azure-storage-creds` (inside each workspace namespace) |
| NetworkPolicies | **50** | `default-deny` (inside each workspace namespace) |

#### Session resources — created on demand, deleted on stop

Each user launching one app creates 3 K8s resources:

| K8s Resource | 10 users × 1 app per workspace | 10 users × 2 apps per workspace |
|---|---|---|
| Deployments | 10 | 20 |
| Services (ClusterIP) | 10 | 20 |
| Ingress rules | 10 | 20 |

**At full load — all 500 users running both Jupyter and RStudio simultaneously:**

| Resource | Count |
|---|---|
| Running Pods | **1,000** |
| Deployments | **1,000** |
| Services (ClusterIP) | **1,000** |
| Ingress rules | **1,000** |

---

### Visual Layout

```
Azure Subscription
│
├── Resource Group: srw-platform-rg
│   │
│   ├── AKS Cluster: srw-aks                        ← 1 cluster total
│   │   │
│   │   ├── Namespace: srw-platform                 ← SRW API pod
│   │   ├── Namespace: ingress-nginx                ← nginx ingress controller
│   │   │
│   │   ├── Namespace: ws-alpha-a1b2c3              ← Workspace 1
│   │   │   ├── Secret:        azure-storage-creds
│   │   │   ├── NetworkPolicy: default-deny
│   │   │   ├── Deployment:    sess-aabbccddee      ← User 1 · Jupyter
│   │   │   ├── Service:       svc-aabbccddee
│   │   │   ├── Ingress:       sess-aabbccddee      (path /s/aabbccddee)
│   │   │   ├── Deployment:    sess-ffgghhiijj      ← User 1 · RStudio
│   │   │   ├── Service:       svc-ffgghhiijj
│   │   │   ├── Ingress:       sess-ffgghhiijj      (path /s/ffgghhiijj)
│   │   │   └── ... × 10 users × 2 apps = up to 20 deployments
│   │   │
│   │   ├── Namespace: ws-beta-d4e5f6               ← Workspace 2
│   │   │   └── ... same structure
│   │   │
│   │   └── ... × 50 namespaces
│   │
│   ├── Storage Account: srwabcdef1234567890ab       ← Workspace 1
│   │   └── File Share: workspace-share
│   │       ├── user-alice/
│   │       ├── user-bob/
│   │       └── ... × 10 user directories
│   │
│   ├── Storage Account: srwghijkl0987654321cd       ← Workspace 2
│   │   └── ... same structure
│   │
│   └── ... × 50 storage accounts
│
├── Service Bus Namespace: srw-servicebus            ← 1 namespace total
│   ├── Queue: srw-workspace-provision
│   ├── Queue: srw-session-stop
│   └── Queue: srw-workspace-cleanup
│
└── Cosmos DB Account: srw-cosmos                   ← 1 account total
    └── Database: srw
        ├── Container: workspaces  (50 documents)
        ├── Container: secrets     (50 documents)
        └── Container: sessions    (grows with usage, up to ~1,000 active)
```

---

### AKS Node Sizing Guidance

Default resource requests per session pod (from `WorkspaceApplication` defaults):

| Resource | Request | Limit |
|---|---|---|
| CPU | 500m | 2 cores |
| Memory | 1 Gi | 4 Gi |

At full load (1,000 concurrent pods):

| Metric | Value |
|---|---|
| Total CPU requested | 1,000 × 500m = **500 cores** |
| Total memory requested | 1,000 × 1 Gi = **1,000 Gi** |

In practice, not all users are active simultaneously. Size your node pool based on **expected peak concurrent sessions**:

| Concurrent Sessions | CPU Requested | Memory Requested | `Standard_D8s_v3` nodes (8 vCPU · 32 GB) |
|---|---|---|---|
| 100 | 50 cores | 100 Gi | ~13 nodes |
| 200 | 100 cores | 200 Gi | ~25 nodes |
| 500 | 250 cores | 500 Gi | ~63 nodes |
| 1,000 (max) | 500 cores | 1,000 Gi | ~125 nodes |

Enable **AKS Cluster Autoscaler** so nodes scale up when sessions are launched and scale down when idle.

---

### Summary Table

| Resource | 50 WS · 10 users · all idle | 50 WS · 10 users · all active (2 apps each) |
|---|---|---|
| AKS Clusters | 1 | 1 |
| K8s Namespaces | 52 (50 + platform + nginx) | 52 |
| K8s Deployments | 0 | 1,000 |
| K8s Services | 0 | 1,000 |
| K8s Ingress rules | 0 | 1,000 |
| Running Pods | 0 | 1,000 |
| K8s Secrets | 50 | 50 |
| K8s NetworkPolicies | 50 | 50 |
| Azure Storage Accounts | 50 | 50 |
| Azure File Shares | 50 | 50 |
| Service Bus Namespaces | 1 | 1 |
| Service Bus Queues | 3 | 3 |
| Cosmos DB Accounts | 1 | 1 |
| Cosmos Documents (total) | ~100 | ~1,100 |

The architecture uses **1 AKS cluster**, **1 Cosmos DB account**, and **1 Service Bus namespace** for the entire platform. Isolation between workspaces is enforced through Kubernetes namespaces and NetworkPolicies, not by provisioning separate clusters per workspace.
