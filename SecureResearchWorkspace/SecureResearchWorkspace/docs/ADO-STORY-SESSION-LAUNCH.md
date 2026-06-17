# ADO Story: Workspace Application Launch

**Epic:** Secure Research Workspace — Session Management  
**Story Type:** Feature / User Story  
**Story Points:** 26  
**Total Estimated Hours:** 82 h

---

## Story Title

`[SRW] Workspace Application Launch — AKS Namespace Provisioning and Per-User Session Management within an Existing TRE Workspace`

---

## Description

### Business Context

The platform integrates with **Azure TRE**, which provisions the workspace isolation boundary on the Azure side — a dedicated Storage Account and SMB File Share per workspace. This story delivers everything above that layer that TRE does not provide: registering the workspace into AKS (namespace, network isolation, storage credentials), defining launchable applications per workspace, and allowing each researcher to launch their own isolated application session (Jupyter Notebook, RStudio, or custom Docker application) and access it via browser with per-user data isolation on the shared File Share.

### What Azure TRE Already Provides

| Resource | Provisioned by |
|---|---|
| Azure Storage Account (`srw<guid>`) | Azure TRE |
| Azure File Share (`workspace-share`) | Azure TRE |
| Storage account key (available as output) | Azure TRE |

### What This Story Adds (All New Work)

| Capability | Notes |
|---|---|
| AKS namespace per workspace | Terraform: `kubernetes_namespace`, default-deny `NetworkPolicy`, `azure-storage-creds` K8s Secret (feeds the CSI driver with TRE's storage key) |
| Application catalogue per workspace | `WorkspaceApplication` entity: type, container image, resource limits, mount path |
| Per-user session launch | `POST /sessions` → K8s Deployment + ClusterIP Service + Ingress rule per user session |
| Per-user storage isolation | CSI volume mounted with `sub_path = sanitize(userId)` so researchers cannot access each other's data |
| Async non-blocking API | POST returns `202 Accepted` immediately; Terraform runs in background worker; client polls status |
| Session lifecycle management | `Starting → Running → Stopping → Stopped / Failed` driven by K8s pod readiness |
| Session stop | `DELETE /sessions/{id}` → Service Bus → consumer tears down K8s resources |
| Idle session cleanup | Background reaper terminates sessions beyond configurable idle threshold |
| Browser access via ingress | Path-based nginx-ingress routing with app-type-specific behaviour (Jupyter / RStudio / Custom) |
| React UI — application gallery | Workspace detail page shows application icons; click launches session via API |
| React UI — session launch flow | App icon transitions through Idle → Launching → Starting → Running states with spinner and progress |
| React UI — session status polling | `useSessionPoller` hook calls `GET /sessions/{id}` at interval until Running or Failed |
| React UI — active sessions panel | Shows the user's current active sessions per workspace with Open and Stop actions |
| React UI — session stop | Stop button with confirmation; calls `DELETE /sessions/{id}`; updates icon state |

---

## User Stories

> **As a platform operator**, after Azure TRE provisions a workspace's storage account, I want the platform to automatically register that workspace into AKS — creating a dedicated namespace with network isolation and the storage credentials secret — so that researchers can immediately start launching applications without manual K8s setup.

> **As a workspace admin**, I want to define which applications are available in my workspace (image, CPU/memory limits, mount path), so that I control the tools researchers use within that workspace.

> **As a researcher assigned to a workspace**, I want to launch a Jupyter Notebook or RStudio session and get a browser URL, so that I can analyse data in an isolated, governed environment.

> **As a researcher**, I want my session to be queued in the background and get the URL once it is ready, so that I am not blocked waiting at a loading screen for several minutes.

> **As a researcher**, I want my files to be private from other researchers in the same workspace, even though we share the same underlying Azure File Share.

> **As a researcher**, I want to click an application icon in my workspace and have it launch automatically, so that I do not need to know about API calls or session IDs.

> **As a researcher**, I want to see a progress indicator while my session is starting (it can take 2–3 minutes), so that I know the system is working and I do not click launch again.

> **As a researcher**, I want to see my already-running sessions when I return to the workspace page, so that I can re-open them without launching a new one.

> **As a researcher**, I want a Stop button to shut down a session I no longer need, so that resources are freed for other researchers.

---

## Architecture

### Where This Sits in the Overall Flow

```
Azure TRE
  └── Provisions:  Storage Account  +  File Share  +  Storage Key
                            │
                            ▼
  SRW Platform (this story)
  └── Step 1: AKS Namespace Provisioning (workspace-k8s Terraform module)
        ├── kubernetes_namespace  (ws-<name>-<id>)
        ├── kubernetes_secret     (azure-storage-creds ← TRE's storage key)
        └── kubernetes_network_policy (default-deny, ingress-nginx only)

  └── Step 2: Application Catalogue
        └── WorkspaceApplication records registered per workspace

  └── Step 3: Per-User Session Launch (session Terraform module per user)
        ├── kubernetes_deployment  (sess-<8-char-id>)
        │     └── VolumeMount → workspace-share / sub_path=sanitize(userId)
        ├── kubernetes_service     (ClusterIP svc-<8-char-id>)
        └── kubernetes_ingress_v1  (path: /s/<slug>/…)
```

### Clean Architecture Layers Touched

```
Domain          → WorkspaceApplication, UserSession, SessionStatus enum
Core            → SessionLauncher, ISessionProvisioningQueue
Infrastructure  → SessionLaunchWorker, SessionStopConsumer,
                  SessionStatusPoller, IdleSessionReaper,
                  TerraformOrchestrator (workspace-k8s + session modules)
API             → SessionEndpoints, ApplicationEndpoints
Terraform       → modules/workspace-k8s/, modules/session/
```

### Terraform State (Azure Blob Remote Backend)

```
workspaces/{k8sNamespace}-k8s.tfstate     ← namespace + secret + NetworkPolicy
sessions/{sessionId}.tfstate              ← Deployment + Service + Ingress per session
```

### Session Status Flow Visible to Clients

```
POST /sessions          → 202 Accepted  { status: "Starting", accessUrl: null }
GET  /sessions/{id}     → { status: "Starting" }   (Terraform running, ~2-3 min)
GET  /sessions/{id}     → { status: "Starting" }   (pod spinning up)
GET  /sessions/{id}     → { status: "Running",  accessUrl: "https://…/s/<slug>/" }
DELETE /sessions/{id}   → 202 Accepted  (teardown queued via Service Bus)
GET  /sessions/{id}     → { status: "Stopped" }
```

---

## Acceptance Criteria

### 1 — AKS Namespace Provisioning
- [ ] After TRE provisions a workspace's Storage Account and File Share, a dedicated AKS namespace is created (`ws-<name>-<id>`) labelled with `srw.io/workspace-id` and `srw.io/managed-by=srw-terraform`
- [ ] A K8s Secret `azure-storage-creds` is created in the workspace namespace, containing the storage account name and key from TRE's output
- [ ] A default-deny `NetworkPolicy` is applied — only traffic from the `ingress-nginx` namespace is allowed inbound
- [ ] Namespace provisioning state is stored in Azure Blob as `workspaces/{k8sNamespace}-k8s.tfstate`
- [ ] The storage account key is never written to disk as a Terraform variable file — passed as `TF_VAR_storage_account_key` environment variable only
- [ ] If namespace provisioning fails, the workspace status reflects `Failed` in the platform database

### 2 — Application Catalogue
- [ ] A workspace admin can register an application via `POST /api/workspaces/{id}/applications` with: `name`, `type`, `containerImage`, `containerPort`, resource limits, `mountPath`, optional `environmentJson`, optional `commandJson`
- [ ] Only applications belonging to the target workspace can be launched within that workspace
- [ ] A workspace must be in `Active` status before applications can be registered or launched

### 3 — Session Launch (API)
- [ ] `POST /api/workspaces/{id}/sessions` returns `202 Accepted` in under 2 seconds regardless of how long Terraform takes
- [ ] Response body includes `{ "id": "…", "status": "Starting", "accessUrl": null }` and a `Location` header pointing to `GET /sessions/{id}`
- [ ] The call is idempotent: a second POST for the same workspace/application/user returns the existing `Starting` or `Running` session — no duplicate deployments created

### 4 — Session Status
- [ ] `GET /api/workspaces/{id}/sessions/{id}` returns current status (`Starting`, `Running`, `Stopped`, `Failed`)
- [ ] Status transitions to `Running` and `accessUrl` is populated once the pod has at least one ready replica
- [ ] If Terraform provisioning fails, status transitions to `Failed` — session does not remain stuck at `Starting` indefinitely

### 5 — Per-User Storage Isolation
- [ ] Two researchers in the same workspace each see only their own files at the container mount path
- [ ] CSI volume mount uses `sub_path = sanitize(userId)` — only `[a-z0-9._-]` characters; all others replaced with `-`
- [ ] Per-user subdirectory is created automatically on first mount — no manual pre-creation required
- [ ] Researcher A cannot navigate to Researcher B's subdirectory from within their running session

### 6 — Browser Access / Ingress
- [ ] Sessions are accessible in a browser at the URL returned in `accessUrl`
- [ ] Jupyter sessions served with `--NotebookApp.base_url=/s/<slug>/` — nginx does not rewrite the path
- [ ] RStudio sessions have `www-root-path=/s/<slug>/` injected into `rserver.conf` at startup; nginx rewrites `/$2`
- [ ] Custom application sessions support `__BASE_URL__` token in `commandJson`, substituted at Terraform apply time
- [ ] WebSocket connections maintained — nginx proxy timeouts set to 3600 s

### 7 — Session Stop
- [ ] `DELETE /api/workspaces/{id}/sessions/{id}` returns `202 Accepted` immediately
- [ ] K8s Deployment, Service, and Ingress destroyed asynchronously via Service Bus consumer
- [ ] Session status transitions to `Stopped` in DB after teardown completes
- [ ] Calling DELETE on an already-`Stopped` session returns `204 No Content` without error

### 8 — Idle Cleanup
- [ ] Sessions beyond the configured idle threshold are automatically stopped by `IdleSessionReaper`
- [ ] Idle threshold configurable via `appsettings.json` (`BackgroundJobs:IdleSessionThresholdMinutes`)

### 9 — React UI

- [ ] Workspace detail page displays all enabled applications for the workspace as clickable icons/cards
- [ ] Clicking an application icon that has no active session calls `POST /sessions` and immediately transitions the icon to a **Starting** state (spinner overlay) — the click is not blocked waiting for Terraform
- [ ] While a session is `Starting`, the icon shows a progress spinner and a "Starting…" label; a second click on the same icon does not launch a duplicate session (idempotency enforced in UI and API)
- [ ] Once the session transitions to `Running`, the spinner is replaced by an **Open** button; clicking it opens `accessUrl` in a new browser tab
- [ ] If provisioning fails (`Failed` status), the icon shows an error state with a **Retry** option that calls `POST /sessions` again
- [ ] When a researcher returns to the workspace page, any already `Running` or `Starting` sessions for their user are shown with the correct state — no stale "idle" icons for active sessions
- [ ] Each application card shows an **active session badge** when a session is `Running`, with an **Open** button and a **Stop** button
- [ ] Clicking **Stop** shows a confirmation dialog; on confirm, calls `DELETE /sessions/{id}` and transitions the icon back to the idle/launch state
- [ ] Polling stops automatically once a session reaches a terminal state (`Running`, `Failed`, `Stopped`) — no perpetual background requests
- [ ] All API calls handle error responses gracefully — network errors and non-2xx responses surface a user-readable message, not a blank screen or unhandled exception
- [ ] UI is consistent with the existing React component/design system in the application

### 10 — Non-Functional
- [ ] Cancelling the HTTP request does not leave a `terraform.exe` process running — process tree is killed on `CancellationToken` fire
- [ ] Each background provisioning operation runs in its own DI scope
- [ ] `dotnet build` passes with zero errors
- [ ] Clean Architecture dependency rule not violated: `Core` contains no reference to Infrastructure types

---

## API Contract

### Register an Application
```
POST /api/workspaces/{workspaceId}/applications
{
  "name": "Jupyter SciPy",
  "type": "Jupyter",
  "containerImage": "jupyter/scipy-notebook:latest",
  "containerPort": 8888,
  "cpuRequest": "500m",  "cpuLimit": "2",
  "memoryRequest": "1Gi", "memoryLimit": "4Gi",
  "mountPath": "/home/jovyan/work"
}
→ 201 Created  { "id": "<guid>", "name": "Jupyter SciPy", … }
```

### Launch a Session
```
POST /api/workspaces/{workspaceId}/sessions
{ "applicationId": "<guid>" }

→ 202 Accepted
Location: /api/workspaces/{workspaceId}/sessions/{sessionId}
{ "id": "…", "status": "Starting", "accessUrl": null, "createdAtUtc": "…" }
```

### Poll Session Status
```
GET /api/workspaces/{workspaceId}/sessions/{sessionId}
→ 200 OK
{ "id": "…", "status": "Running", "accessUrl": "https://research.example.com/s/a7fca0c050/" }
```

### Stop a Session
```
DELETE /api/workspaces/{workspaceId}/sessions/{sessionId}
→ 202 Accepted
```

---

## Key Files

| File | What it does |
|---|---|
| `terraform/modules/workspace-k8s/main.tf` | Namespace + `azure-storage-creds` Secret + default-deny NetworkPolicy |
| `terraform/modules/session/main.tf` | K8s Deployment + Service + Ingress; CSI volume with per-user `sub_path` |
| `terraform/modules/session/app_configs.tf` | Per-app-type startup command, base_url, ingress rewrite logic |
| `src/SRW.Domain/Entities/WorkspaceApplication.cs` | Application template entity |
| `src/SRW.Domain/Entities/UserSession.cs` | Session entity; pre-seeds K8s resource names at creation |
| `src/SRW.Core/Abstractions/ISessionProvisioningQueue.cs` | Core-layer interface for enqueueing background launch |
| `src/SRW.Core/Services/SessionLauncher.cs` | Validates, creates Starting session, enqueues — returns immediately |
| `src/SRW.Infrastructure/Terraform/TerraformOrchestrator.cs` | `EnsureWorkspaceNamespaceAsync` + `LaunchSessionAsync` + `StopSessionAsync` via Terraform CLI |
| `src/SRW.Infrastructure/Terraform/TerraformRunner.cs` | Low-level shell executor — init/apply/destroy/output |
| `src/SRW.Infrastructure/BackgroundJobs/SessionLaunchWorker.cs` | BackgroundService — dequeues, runs Terraform apply, updates session |
| `src/SRW.Infrastructure/BackgroundJobs/SessionStatusPoller.cs` | Polls K8s pod readiness → transitions session to Running |
| `src/SRW.Infrastructure/BackgroundJobs/SessionStopConsumer.cs` | Service Bus consumer → terraform destroy → marks Stopped |
| `src/SRW.Infrastructure/BackgroundJobs/IdleSessionReaper.cs` | Reaps sessions beyond idle threshold |
| `src/SRW.Api/Endpoints/ApplicationEndpoints.cs` | Register + list applications per workspace |
| `src/SRW.Api/Endpoints/SessionEndpoints.cs` | POST / GET / DELETE sessions |
| `src/SRW.Api/Program.cs` | DI wiring for all new services |

---

## Dependencies

- Azure TRE has provisioned the workspace Storage Account, File Share, and storage key before AKS namespace provisioning begins
- AKS cluster has nginx-ingress and Azure File CSI driver installed (one-time cluster setup, not per workspace)
- Terraform CLI binary available to the API process (`appsettings.json → Terraform:TerraformBinaryPath`)
- Azure Blob Storage container for Terraform remote state (`Terraform:StateStorageAccount / StateContainer`)

---

## Technical Notes

**Why AKS namespace is not part of TRE:**
TRE manages Azure-plane resources (Storage Account, File Share). AKS namespace management — including binding TRE's storage credentials into a K8s Secret for the CSI driver and enforcing workspace-level network isolation — is platform-layer responsibility done via Terraform's `kubernetes` provider, separate from the `azurerm` provider TRE uses.

**Storage key security:**
The storage account key from TRE is passed to Terraform exclusively as `TF_VAR_storage_account_key` (environment variable). It is never written to `terraform.tfvars` on disk.

**Why in-process `Channel<T>` for session launch, not Service Bus:**
Session launch is initiated from the same API process. A `System.Threading.Channels` unbounded channel gives sub-millisecond enqueue latency and requires no external broker. Service Bus is reserved for stop operations (which must survive a pod restart mid-teardown) and workspace cleanup (cross-process coordination).

**Ingress routing — why Jupyter and RStudio behave differently:**
RStudio does not support a `--base-url` startup argument. Instead, `www-root-path` must be written to `rserver.conf` before the process starts and nginx must strip the path prefix via `rewrite-target: /$2`. Jupyter handles the prefix natively via `--NotebookApp.base_url`, so nginx passes the full path through without rewriting.

---

## Definition of Done

- [ ] All acceptance criteria met
- [ ] Jupyter session: launched end-to-end, reached Running, notebook accessible in browser, files persisted to user subdirectory
- [ ] RStudio session: path prefix reflected correctly, no broken asset URLs
- [ ] Two-user isolation verified: researchers A and B in the same workspace cannot see each other's files
- [ ] Workspace namespace teardown tested: deleting a workspace tears down the K8s namespace and CSI secret
- [ ] `dotnet build` clean, no warnings
- [ ] ADO child tasks linked and closed

---

---

# ADO Tasks

> All tasks below are child items of the story above.  
> Total: **21 tasks — 61 estimated hours**

---

## Task Summary

| # | Area | Title | Est. Hours |
|---|---|---|---|
| T-01 | Terraform | workspace-k8s module — Namespace + CSI Secret + NetworkPolicy | 4 h |
| T-02 | Terraform | session module — K8s Deployment + Service + Ingress | 6 h |
| T-03 | Terraform | session module — app-type startup logic (Jupyter / RStudio / Custom) | 4 h |
| T-04 | Domain | `WorkspaceApplication` entity | 2 h |
| T-05 | Domain | `UserSession` entity + `SessionStatus` enum | 2 h |
| T-06 | Core | `ISessionProvisioningQueue` interface | 1 h |
| T-07 | Core | `SessionLauncher` — validate, persist, enqueue, return fast | 3 h |
| T-08 | Infrastructure | `TerraformRunner` — backend file, init-skip, process kill on cancel | 4 h |
| T-09 | Infrastructure | `TerraformOrchestrator` — `EnsureWorkspaceNamespaceAsync` | 3 h |
| T-10 | Infrastructure | `TerraformOrchestrator` — `LaunchSessionAsync` + `StopSessionAsync` | 4 h |
| T-11 | Infrastructure | `SessionLaunchWorker` — background channel worker | 4 h |
| T-12 | Infrastructure | `SessionStatusPoller` — K8s pod readiness → Running transition | 3 h |
| T-13 | Infrastructure | `SessionStopConsumer` — Service Bus → terraform destroy | 3 h |
| T-14 | Infrastructure | `IdleSessionReaper` — idle threshold cleanup | 2 h |
| T-15 | API | `ApplicationEndpoints` — register + list applications | 2 h |
| T-16 | API | `SessionEndpoints` — POST / GET / DELETE sessions | 3 h |
| T-17 | API | `Program.cs` — DI registration for all new services | 2 h |
| T-18 | Test | E2E — Jupyter session launch, browser access, file persistence | 3 h |
| T-19 | Test | E2E — RStudio session, path prefix, UI correctness | 2 h |
| T-20 | Test | E2E — Per-user storage isolation (two users, same workspace) | 2 h |
| T-21 | Test | E2E — Session stop and idle reaper cleanup | 2 h |
| T-22 | UI | Session API client (`sessionApi.ts`) — typed fetch wrappers for POST / GET / DELETE | 2 h |
| T-23 | UI | `useSessionPoller` hook — polls `GET /sessions/{id}` at interval until terminal state | 3 h |
| T-24 | UI | `AppCard` component — app icon with status overlay (Idle / Starting / Running / Failed) | 4 h |
| T-25 | UI | Workspace applications page — gallery of `AppCard` components, load existing sessions on mount | 3 h |
| T-26 | UI | Session launch flow — click handler: POST → Starting state → poller → Running / Failed state | 3 h |
| T-27 | UI | Active sessions panel — list of Running sessions with Open and Stop actions | 3 h |
| T-28 | UI | Session stop flow — Stop button, confirmation dialog, DELETE call, icon reset | 3 h |
| | | **Total** | **82 h** |

---

## T-01 — Terraform: workspace-k8s module

**Title:** `Terraform — workspace-k8s module: K8s Namespace + CSI Secret + NetworkPolicy`  
**Area:** Infrastructure / Terraform  
**Estimated Hours:** 4 h  
**Dependencies:** None

### Description

Create the Terraform module `terraform/modules/workspace-k8s/` that provisions AKS-side resources for a workspace after Azure TRE has created the Storage Account and File Share. This module is invoked once per workspace. State stored at `workspaces/{k8sNamespace}-k8s.tfstate`.

Resources to declare:

- `kubernetes_namespace` — named `ws-<name>-<id>`, labelled with `srw.io/workspace-id` and `srw.io/managed-by=srw-terraform`
- `kubernetes_secret` — named `azure-storage-creds`, holds `azurestorageaccountname` and `azurestorageaccountkey` consumed by the Azure File CSI driver
- `kubernetes_network_policy` — `default-deny` on the namespace; allows inbound only from the `ingress-nginx` namespace

Input variables: `workspace_id`, `k8s_namespace`, `storage_account_name`, `storage_account_key`

**Security:** `storage_account_key` must be accepted as a sensitive variable and passed at runtime via `TF_VAR_storage_account_key` — never written to `terraform.tfvars` on disk.

### Acceptance Criteria

- [ ] `terraform plan` produces exactly 3 resource creations: namespace, secret, network policy
- [ ] Secret contains correct `azurestorageaccountname` and `azurestorageaccountkey` values
- [ ] No pod inside the namespace can receive traffic except from `ingress-nginx` namespace
- [ ] Storage account key does not appear in any `.tfvars` file or Terraform state in plaintext

---

## T-02 — Terraform: session module — Deployment + Service + Ingress

**Title:** `Terraform — session module: K8s Deployment + ClusterIP Service + Ingress per session`  
**Area:** Infrastructure / Terraform  
**Estimated Hours:** 6 h  
**Dependencies:** T-01

### Description

Create the Terraform module `terraform/modules/session/` that provisions per-user, per-session K8s resources. Invoked once per session launch; destroyed on session stop. State stored at `sessions/{sessionId}.tfstate`.

Resources to declare:

- `kubernetes_deployment` — single-replica pod; mounts workspace Azure File Share via CSI driver with `sub_path = local.sanitized_user` for per-user isolation; configurable resource requests and limits
- `kubernetes_service` — ClusterIP, port 80 → `container_port`
- `kubernetes_ingress_v1` — nginx ingress class; path `{ingressPath}(/|$)(.*)`; proxy timeout annotations (3600 s); WebSocket support (`proxy-http-version: 1.1`); conditional `rewrite-target` for RStudio

CSI volume mount:
```hcl
volume_mount {
  mount_path = var.mount_path
  sub_path   = local.sanitized_user   # [^a-z0-9._-] replaced with -
}
```

Output variables: `deployment_name`, `service_name`, `access_url`

Input variables: `session_id`, `workspace_id`, `user_id`, `app_type`, `k8s_namespace`, `ingress_path`, `ingress_domain`, `container_image`, `container_port`, `cpu_request`, `cpu_limit`, `memory_request`, `memory_limit`, `mount_path`, `file_share_name`, `environment_json`, `command_json`

### Acceptance Criteria

- [ ] `terraform plan` produces exactly 3 resource creations: deployment, service, ingress
- [ ] Pod mounts the file share at the correct `mount_path` with `sub_path` set to sanitized user ID
- [ ] Two concurrent sessions for different users in the same namespace mount different `sub_path` values
- [ ] Outputs `deployment_name`, `service_name`, and `access_url` are correct
- [ ] Ingress path regex matches `/s/<slug>/` and sub-paths correctly

---

## T-03 — Terraform: session module — app-type startup logic

**Title:** `Terraform — session module: per-app-type startup command (Jupyter / RStudio / Custom)`  
**Area:** Infrastructure / Terraform  
**Estimated Hours:** 4 h  
**Dependencies:** T-02

### Description

Add `app_configs.tf` to the session module containing the `locals` block that drives app-type-specific behaviour. Keeping this separate from `main.tf` means adding a new app type requires changes only in `app_configs.tf`.

**Jupyter:**
- Default command: `start-notebook.sh --NotebookApp.token='' --NotebookApp.base_url={ingressPath}/`
- Custom `command_json` supported with `__BASE_URL__` token substitution
- Nginx does **not** rewrite path — Jupyter handles the prefix itself

**RStudio:**
- Command: bash one-liner that writes `www-root-path={ingressPath}` into `rserver.conf` then `exec /init`
- Nginx rewrites `/$2` via `rewrite-target` annotation (`use_rewrite = true` for RStudio only)

**Custom:**
- Respects `command_json`; `__BASE_URL__` token substituted; no path rewrite

**Ingress host:** if `ingress_domain` is a raw IP address, the `host` block is omitted from the ingress rule.

### Acceptance Criteria

- [ ] Jupyter pod starts serving notebooks at `/s/<slug>/` with no token prompt
- [ ] RStudio pod serves UI correctly at `/s/<slug>/` — no broken asset URLs, console works
- [ ] Custom app receives the substituted base URL in its command
- [ ] IP-based `ingress_domain` (dev environment) generates a valid ingress without a `host` field
- [ ] Adding a fourth app type requires changes only in `app_configs.tf`, not `main.tf`

---

## T-04 — Domain: WorkspaceApplication entity

**Title:** `Domain — WorkspaceApplication entity: application template per workspace`  
**Area:** Domain  
**Estimated Hours:** 2 h  
**Dependencies:** None

### Description

Define the `WorkspaceApplication` entity in `SRW.Domain`. This is the template from which user sessions are created — it is not a running instance; it describes what can be launched.

Fields: `Id`, `WorkspaceId`, `Name`, `Type` (`ApplicationType` enum: Jupyter | RStudio | Custom), `ContainerImage`, `ContainerPort`, `CpuRequest`, `CpuLimit`, `MemoryRequest`, `MemoryLimit`, `MountPath`, `EnvironmentJson`, `CommandJson`, `Enabled`

### Acceptance Criteria

- [ ] Entity has no external dependencies — `SRW.Domain` project references only `System` types
- [ ] Default resource values are sensible: `CpuRequest=500m`, `CpuLimit=2`, `MemoryRequest=1Gi`, `MemoryLimit=4Gi`, `MountPath=/home/jovyan/work`
- [ ] `Enabled` flag allows an admin to disable an application without deleting it
- [ ] `ApplicationType` enum has members: `Jupyter`, `RStudio`, `Custom`

---

## T-05 — Domain: UserSession entity + SessionStatus enum

**Title:** `Domain — UserSession entity and SessionStatus enum`  
**Area:** Domain  
**Estimated Hours:** 2 h  
**Dependencies:** None

### Description

Define the `UserSession` entity and `SessionStatus` enum in `SRW.Domain`. A `UserSession` is a running instance of a `WorkspaceApplication` for a specific user — it maps 1:1 to one K8s Deployment + Service + Ingress rule.

Fields: `Id`, `WorkspaceId`, `ApplicationId`, `UserId`, `Status`, `DeploymentName`, `ServiceName`, `IngressPath`, `AccessUrl`, `CreatedAtUtc`, `StartedAtUtc`, `StoppedAtUtc`, `LastActivityUtc`

`SessionStatus` enum: `Pending`, `Starting`, `Running`, `Stopping`, `Stopped`, `Failed`

The `UserSession.Create()` factory pre-seeds `DeploymentName` (`sess-<8hex>`), `ServiceName` (`svc-<8hex>`), and `IngressPath` (`/s/<10hex>`) from the new session GUID. These values are **overwritten** by actual Terraform output values after provisioning (T-10) — Terraform is the authoritative source of K8s resource names.

### Acceptance Criteria

- [ ] `Create()` produces unique, K8s-safe names derived from the session GUID
- [ ] No external dependencies in `SRW.Domain`
- [ ] `DeploymentName` and `ServiceName` are overwritten after Terraform output is captured — not relied on for K8s lookups until updated

---

## T-06 — Core: ISessionProvisioningQueue interface

**Title:** `Core — ISessionProvisioningQueue: domain interface for background session launch`  
**Area:** Core / Abstractions  
**Estimated Hours:** 1 h  
**Dependencies:** T-04, T-05

### Description

Define `ISessionProvisioningQueue` in `SRW.Core.Abstractions`. This interface allows `SessionLauncher` (Core layer) to enqueue a session for background provisioning without referencing any Infrastructure type, preserving the Clean Architecture dependency rule.

```csharp
namespace SRW.Core.Abstractions;

public interface ISessionProvisioningQueue
{
    void EnqueueLaunch(Guid sessionId, Guid workspaceId, Guid applicationId, string userId);
}
```

The implementation (`SessionLaunchWorker`) lives in `SRW.Infrastructure` (T-11).

### Acceptance Criteria

- [ ] Interface defined in `SRW.Core.Abstractions` namespace
- [ ] `SRW.Core` project has no project reference to `SRW.Infrastructure`
- [ ] Method is synchronous (`void`) — enqueue must never block the HTTP request thread

---

## T-07 — Core: SessionLauncher service

**Title:** `Core — SessionLauncher: validate + persist Starting session + enqueue provisioning`  
**Area:** Core / Services  
**Estimated Hours:** 3 h  
**Dependencies:** T-04, T-05, T-06

### Description

Implement `SessionLauncher` in `SRW.Core.Services`. This service handles the synchronous portion of session launch — it runs inside the HTTP request thread and must return fast.

Sequence:
1. Load workspace; throw `InvalidOperationException` if not found or not `Active`
2. Load application; throw if not found or does not belong to the workspace
3. Idempotency check — return existing session if status is `Starting` or `Running`
4. Create `UserSession` with `Status = Starting`; persist to `ISessionRepository`
5. Call `ISessionProvisioningQueue.EnqueueLaunch(...)` — synchronous enqueue, no await
6. Return the session record; HTTP layer returns `202 Accepted`

Injected dependencies: `IWorkspaceRepository`, `ISessionRepository`, `ISessionProvisioningQueue`, `ILogger<SessionLauncher>`

### Acceptance Criteria

- [ ] `LaunchAsync` completes within milliseconds regardless of Terraform duration
- [ ] Session record exists in DB with `Status = Starting` immediately after return
- [ ] A second call for the same workspace/application/user returns the existing session — does not create a duplicate DB record or enqueue a duplicate provisioning job
- [ ] `IKubernetesOrchestrator` is not injected — orchestration is Infrastructure's responsibility

---

## T-08 — Infrastructure: TerraformRunner

**Title:** `Infrastructure — TerraformRunner: backend file, init-skip optimisation, process kill on cancel`  
**Area:** Infrastructure / Terraform  
**Estimated Hours:** 4 h  
**Dependencies:** None

### Description

Implement `TerraformRunner` in `SRW.Infrastructure.Terraform` — the low-level shell executor wrapping Terraform CLI calls, used by `TerraformOrchestrator` (T-09, T-10).

**`InitAsync`:**
- Copies module `.tf` files into a per-resource working directory (`{WorkingRootDir}/{module}/{safeKey}/`)
- Writes `backend.tfbackend` (HCL key-value file) instead of inline `-backend-config="key=val"` arguments
- Skips `terraform init` if `.terraform/` already exists in the working dir — saves 20–60 s per repeated operation

**`DestroyAsync`:**
- Checks for `backend.tfbackend` and `.terraform/` before running; re-runs init if `.terraform/` is absent (handles restart recovery)

**`ExecAsync` — process kill on cancellation:**
```csharp
catch (OperationCanceledException)
{
    try { process.Kill(entireProcessTree: true); } catch { }
    throw;
}
```

### Acceptance Criteria

- [ ] `terraform init` is not called if `.terraform/` already exists in the working directory
- [ ] Backend config is written as `backend.tfbackend` HCL file, not inline args
- [ ] Cancelling a long-running `terraform apply` leaves no orphaned `terraform.exe` process
- [ ] Working directory layout: `{WorkingRootDir}/{module}/{safeKey}/`

---

## T-09 — Infrastructure: TerraformOrchestrator — workspace namespace

**Title:** `Infrastructure — TerraformOrchestrator: EnsureWorkspaceNamespaceAsync (workspace-k8s module)`  
**Area:** Infrastructure / Terraform  
**Estimated Hours:** 3 h  
**Dependencies:** T-01, T-08

### Description

Add `EnsureWorkspaceNamespaceAsync` and `DeleteWorkspaceNamespaceAsync` to `TerraformOrchestrator`. Called by the workspace provisioning flow after TRE has created the Storage Account and File Share.

- Invokes the `workspace-k8s` Terraform module (T-01) via `TerraformRunner`
- State key: `workspaces/{k8sNamespace}-k8s.tfstate`
- Storage account key passed as sensitive environment variable `TF_VAR_storage_account_key` — never in `.tfvars`
- `DeleteWorkspaceNamespaceAsync` runs `terraform destroy` on the workspace-k8s working directory

### Acceptance Criteria

- [ ] After `EnsureWorkspaceNamespaceAsync` completes, `kubectl get ns` shows the workspace namespace
- [ ] `kubectl get secret azure-storage-creds -n {namespace}` returns correct values
- [ ] Storage key does not appear in any file in the working directory after the call returns
- [ ] `DeleteWorkspaceNamespaceAsync` removes the namespace and all resources within it

---

## T-10 — Infrastructure: TerraformOrchestrator — session lifecycle

**Title:** `Infrastructure — TerraformOrchestrator: LaunchSessionAsync + StopSessionAsync + GetSessionStatusAsync`  
**Area:** Infrastructure / Terraform  
**Estimated Hours:** 4 h  
**Dependencies:** T-02, T-03, T-08

### Description

Add session lifecycle methods to `TerraformOrchestrator`:

**`LaunchSessionAsync`:**
- Builds vars from `Workspace`, `WorkspaceApplication`, and `UserSession` fields
- Calls `TerraformRunner.InitAsync` then `ApplyAsync`
- Reads Terraform outputs: `deployment_name`, `service_name`, `access_url`
- Returns `SessionDeploymentResult(DeploymentName, ServiceName, AccessUrl)`
- The returned `DeploymentName` and `ServiceName` are authoritative — they must overwrite the pre-seeded values from `UserSession.Create()` in the caller (T-11)

**`StopSessionAsync`:** runs `terraform destroy` in the session's working directory

**`GetSessionStatusAsync`:** uses K8s client directly (not Terraform) — checks `ReadyReplicas` on the deployment; returns `Running` if ≥ 1, `Starting` otherwise, `Stopped` if deployment not found

### Acceptance Criteria

- [ ] After `LaunchSessionAsync` returns, `kubectl get deploy -n {namespace}` shows the deployment
- [ ] `result.DeploymentName` matches the actual K8s deployment name (8-char session ID prefix)
- [ ] `result.AccessUrl` is correct for both IP-based (`http://`) and domain-based (`https://`) ingress
- [ ] `StopSessionAsync` removes all 3 K8s resources (deployment, service, ingress)
- [ ] `GetSessionStatusAsync` returns `Running` once pod readiness is confirmed via K8s client

---

## T-11 — Infrastructure: SessionLaunchWorker

**Title:** `Infrastructure — SessionLaunchWorker: background channel worker + ISessionProvisioningQueue implementation`  
**Area:** Infrastructure / BackgroundJobs  
**Estimated Hours:** 4 h  
**Dependencies:** T-05, T-06, T-07, T-08, T-10

### Description

Implement `SessionLaunchWorker` in `SRW.Infrastructure.BackgroundJobs`. This class serves two roles:

- **`ISessionProvisioningQueue`** — enqueue side, called synchronously from `SessionLauncher`
- **`BackgroundService`** — dequeue side, runs `LaunchSessionAsync` in the background

Uses `System.Threading.Channels.Channel<LaunchItem>` (unbounded, single reader) as the internal queue.

Each dequeued item runs in its own DI scope (`IServiceScopeFactory.CreateAsyncScope()`) to avoid sharing scoped repository instances across concurrent operations.

On success: writes `DeploymentName`, `ServiceName`, `AccessUrl`, `StartedAtUtc` to the session record.  
On failure: sets `Status = Failed`.

DI registration (single instance exposed via two interfaces):
```csharp
builder.Services.AddSingleton<SessionLaunchWorker>();
builder.Services.AddSingleton<ISessionProvisioningQueue>(sp => sp.GetRequiredService<SessionLaunchWorker>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionLaunchWorker>());
```

### Acceptance Criteria

- [ ] POST /sessions returns before `SessionLaunchWorker` starts processing the item
- [ ] Session `DeploymentName` and `ServiceName` in DB reflect Terraform output values after provisioning — not the pre-seeded values from `UserSession.Create()`
- [ ] A failed Terraform apply sets session status to `Failed`
- [ ] Application shutdown does not leave a running `terraform.exe` process

---

## T-12 — Infrastructure: SessionStatusPoller

**Title:** `Infrastructure — SessionStatusPoller: K8s pod readiness → session Running transition`  
**Area:** Infrastructure / BackgroundJobs  
**Estimated Hours:** 3 h  
**Dependencies:** T-05, T-10

### Description

Implement `SessionStatusPoller` as a `BackgroundService`. Runs on a configurable interval. For each session in `Starting` status, calls `IKubernetesOrchestrator.GetSessionStatusAsync` (reads live K8s pod readiness, not Terraform state). If the response is `Running`, updates the session in DB to `Running`. This is the only mechanism that drives the `Starting → Running` transition.

### Acceptance Criteria

- [ ] Within `pollInterval + pod startup time`, a launched session transitions from `Starting` to `Running` in DB
- [ ] Poll interval configurable via `appsettings.json` (`BackgroundJobs:SessionStatusPollIntervalSeconds`)
- [ ] A session whose deployment is not found in K8s is not incorrectly set to `Running`
- [ ] Each poll runs in its own DI scope

---

## T-13 — Infrastructure: SessionStopConsumer

**Title:** `Infrastructure — SessionStopConsumer: Service Bus consumer → terraform destroy → Stopped`  
**Area:** Infrastructure / BackgroundJobs  
**Estimated Hours:** 3 h  
**Dependencies:** T-05, T-10

### Description

Implement `SessionStopConsumer` as a `BackgroundService` that listens on the `session-stop` Service Bus queue. On receiving a `SessionStopMessage` (`SessionId`, `WorkspaceId`):

1. Load session from DB — if not found or already `Stopped`, complete the message and return
2. Load workspace to get `K8sNamespace`
3. Call `IKubernetesOrchestrator.StopSessionAsync` — runs `terraform destroy`
4. Set `session.Status = Stopped`, `session.StoppedAtUtc = UtcNow`, persist
5. Complete the Service Bus message

On exception: abandon the message (Service Bus retry / dead-letter after max delivery count).

### Acceptance Criteria

- [ ] After DELETE /sessions/{id}, all 3 K8s resources are removed
- [ ] Session status transitions to `Stopped` in DB after teardown completes
- [ ] If the K8s resources are already gone when stop is processed, the operation completes without error (idempotent destroy)
- [ ] Message is dead-lettered after repeated failures, not retried indefinitely

---

## T-14 — Infrastructure: IdleSessionReaper

**Title:** `Infrastructure — IdleSessionReaper: automatic cleanup of idle sessions`  
**Area:** Infrastructure / BackgroundJobs  
**Estimated Hours:** 2 h  
**Dependencies:** T-05, T-13

### Description

Implement `IdleSessionReaper` as a `BackgroundService`. Runs on a configurable interval. Queries `ISessionRepository` for `Running` sessions where `LastActivityUtc` is older than the configured idle threshold. For each, publishes a `SessionStopMessage` to the Service Bus stop queue so that `SessionStopConsumer` handles teardown — the same code path as an explicit DELETE.

### Acceptance Criteria

- [ ] A session with no activity beyond `IdleSessionThresholdMinutes` is automatically stopped
- [ ] Idle threshold configurable via `appsettings.json` (`BackgroundJobs:IdleSessionThresholdMinutes`)
- [ ] Reaper does not attempt to stop sessions already in `Stopping` or `Stopped` status
- [ ] Reaper interval configurable via `appsettings.json` (`BackgroundJobs:IdleReaperIntervalMinutes`)

---

## T-15 — API: ApplicationEndpoints

**Title:** `API — ApplicationEndpoints: register and list applications per workspace`  
**Area:** API / Endpoints  
**Estimated Hours:** 2 h  
**Dependencies:** T-04

### Description

Implement `ApplicationEndpoints` as a Minimal API extension in `SRW.Api.Endpoints`:

- `POST /api/workspaces/{workspaceId}/applications` — register a new `WorkspaceApplication`; returns `201 Created`
- `GET /api/workspaces/{workspaceId}/applications` — list all enabled applications for a workspace
- `GET /api/workspaces/{workspaceId}/applications/{applicationId}` — get a single application

Request DTO fields: `name`, `type`, `containerImage`, `containerPort`, `cpuRequest`, `cpuLimit`, `memoryRequest`, `memoryLimit`, `mountPath`, `environmentJson`, `commandJson`

### Acceptance Criteria

- [ ] POST validates workspace exists and is `Active` before registering
- [ ] POST returns `400 Bad Request` with descriptive error if workspace not found or not `Active`
- [ ] GET returns only applications belonging to the requested `workspaceId`
- [ ] `ICurrentUser` header (`X-User-Id`) present on all endpoints

---

## T-16 — API: SessionEndpoints

**Title:** `API — SessionEndpoints: POST / GET / DELETE session endpoints`  
**Area:** API / Endpoints  
**Estimated Hours:** 3 h  
**Dependencies:** T-05, T-07, T-13

### Description

Implement `SessionEndpoints` as a Minimal API extension in `SRW.Api.Endpoints`:

- `POST /api/workspaces/{workspaceId}/sessions` — calls `SessionLauncher.LaunchAsync`, returns `202 Accepted` with `Location` header
- `GET /api/workspaces/{workspaceId}/sessions` — lists sessions for the current user in the workspace
- `GET /api/workspaces/{workspaceId}/sessions/{sessionId}` — single session status (polling endpoint)
- `DELETE /api/workspaces/{workspaceId}/sessions/{sessionId}` — sets status to `Stopping`, publishes `SessionStopMessage` to Service Bus, returns `202 Accepted`

`SessionResponse` DTO: `Id`, `WorkspaceId`, `ApplicationId`, `Status`, `AccessUrl`, `CreatedAtUtc`, `StartedAtUtc`

### Acceptance Criteria

- [ ] POST returns `202 Accepted` — never `200 OK`
- [ ] `Location` header on POST response points to `GET /sessions/{id}`
- [ ] GET /sessions/{id} reflects live status from DB, driven by `SessionStatusPoller`
- [ ] DELETE on an already-`Stopped` session returns `204 No Content`, not an error
- [ ] POST returns `400 Bad Request` if workspace not `Active` or application not found

---

## T-17 — API: Program.cs DI Registration

**Title:** `API — Program.cs: DI registration for all new session and workspace-k8s services`  
**Area:** API / Composition Root  
**Estimated Hours:** 2 h  
**Dependencies:** T-06, T-07, T-08, T-09, T-10, T-11, T-12, T-13, T-14, T-15, T-16

### Description

Wire all new services into `Program.cs`:

```csharp
// Terraform runner and orchestrator
builder.Services.AddSingleton<TerraformRunner>();
builder.Services.AddSingleton<TerraformOrchestrator>();
builder.Services.AddSingleton<IAzureStorageProvisioner>(sp => sp.GetRequiredService<TerraformOrchestrator>());
builder.Services.AddSingleton<IKubernetesOrchestrator>(sp => sp.GetRequiredService<TerraformOrchestrator>());

// Session services
builder.Services.AddScoped<SessionLauncher>();

// SessionLaunchWorker: single instance serves as queue and hosted worker
builder.Services.AddSingleton<SessionLaunchWorker>();
builder.Services.AddSingleton<ISessionProvisioningQueue>(sp => sp.GetRequiredService<SessionLaunchWorker>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionLaunchWorker>());

// Background workers
builder.Services.AddHostedService<SessionStatusPoller>();
builder.Services.AddHostedService<SessionStopConsumer>();
builder.Services.AddHostedService<IdleSessionReaper>();

// Configuration
builder.Services.Configure<TerraformOptions>(builder.Configuration.GetSection("Terraform"));
builder.Services.Configure<BackgroundJobOptions>(builder.Configuration.GetSection("BackgroundJobs"));
```

Also ensure `appsettings.Development.json.example` documents all new `Terraform:*` and `BackgroundJobs:*` config keys.

### Acceptance Criteria

- [ ] `dotnet build` produces zero errors and zero warnings
- [ ] All background services start without exception on `dotnet run`
- [ ] `GET /health` returns `200 OK` after startup
- [ ] Swagger shows all new endpoints under the `Sessions` and `Applications` tags

---

## T-18 — E2E Test: Jupyter session launch

**Title:** `Test — E2E: Jupyter session launch, browser access, file persistence`  
**Area:** Test / E2E  
**Estimated Hours:** 3 h  
**Dependencies:** T-15, T-16, T-17

### Description

End-to-end verification of the full Jupyter session flow against a real AKS cluster and Azure TRE-provisioned workspace.

Steps:
1. Call `POST /sessions` with a Jupyter application ID
2. Verify response is `202 Accepted` with `status: "Starting"` returned in under 2 seconds
3. Poll `GET /sessions/{id}` until `status = "Running"` (allow up to 5 minutes)
4. Open `accessUrl` in browser via kubectl port-forward — verify Jupyter UI loads, no 404 or asset errors
5. Create a notebook, save a file — verify it persists to the user's subdirectory on the file share
6. Call `DELETE /sessions/{id}` — verify K8s deployment is torn down and status reaches `Stopped`

### Acceptance Criteria

- [ ] `202 Accepted` returned in under 2 seconds
- [ ] Status reaches `Running` within 5 minutes of POST
- [ ] Jupyter notebook UI loads at `accessUrl` with correct base URL — all links include `/s/<slug>/` prefix
- [ ] File created in session is visible in the user's subdirectory on the file share
- [ ] Session stops cleanly with no orphaned K8s resources

---

## T-19 — E2E Test: RStudio session

**Title:** `Test — E2E: RStudio session launch, path prefix correctness, UI verification`  
**Area:** Test / E2E  
**Estimated Hours:** 2 h  
**Dependencies:** T-15, T-16, T-17, T-18

### Description

End-to-end verification specific to RStudio path prefix behaviour.

Steps:
1. Register an RStudio application (`type: RStudio`, `containerImage: rocker/rstudio:latest`, `containerPort: 8787`)
2. Launch a session and poll to `Running`
3. Open `accessUrl` in browser — verify RStudio IDE loads, no broken asset URLs (CSS/JS), no redirect loop
4. Open RStudio terminal — verify it is functional (WebSocket connection maintained)
5. Verify the `Files` pane shows the user's workspace directory

### Acceptance Criteria

- [ ] RStudio IDE loads at `/s/<slug>/` — no 404 on assets
- [ ] No redirect loop (correct `www-root-path` in `rserver.conf`)
- [ ] Terminal tab opens and accepts commands (WebSocket maintained)
- [ ] Files pane shows only the current user's files

---

## T-20 — E2E Test: Per-user storage isolation

**Title:** `Test — E2E: per-user storage isolation — two researchers, same workspace`  
**Area:** Test / E2E  
**Estimated Hours:** 2 h  
**Dependencies:** T-15, T-16, T-17, T-18

### Description

Verify that two researchers in the same workspace cannot access each other's files despite sharing the same Azure File Share.

Steps:
1. Launch a session as `user-alice` and a separate session as `user-bob` in the same workspace with the same application
2. In Alice's session, create a file `alice-private.txt`
3. In Bob's session, attempt to navigate to Alice's subdirectory
4. Verify Bob cannot see `alice-private.txt` — his mount point shows only his own subdirectory
5. Verify both sessions are active concurrently in the same K8s namespace

### Acceptance Criteria

- [ ] Alice's session mounts file share at `sub_path = alice` (or sanitized equivalent)
- [ ] Bob's session mounts file share at `sub_path = bob`
- [ ] Alice's files are not visible in Bob's session and vice versa
- [ ] Both sessions run concurrently without conflict in the same K8s namespace

---

## T-21 — E2E Test: Session stop and idle reaper

**Title:** `Test — E2E: session stop (DELETE) and idle session reaper cleanup`  
**Area:** Test / E2E  
**Estimated Hours:** 2 h  
**Dependencies:** T-13, T-14, T-17, T-18

### Description

Verify both the explicit stop path and the automatic idle cleanup path.

**Explicit stop:**
1. Launch a session and wait for `Running`
2. Call `DELETE /sessions/{id}`
3. Verify `202 Accepted` returned immediately
4. Poll status — verify transitions to `Stopped` within 3 minutes
5. Verify `kubectl get deploy -n {namespace}` shows no deployment for that session

**Idle reaper:**
1. Set `IdleSessionThresholdMinutes` to a short value (e.g. 2 minutes) in dev config
2. Launch a session, let it reach `Running`, then do not interact
3. Wait for reaper interval + threshold to elapse
4. Verify session status transitions to `Stopped` automatically

### Acceptance Criteria

- [ ] `DELETE` returns `202` immediately — no blocking on K8s teardown
- [ ] All 3 K8s resources (deployment, service, ingress) are removed after stop
- [ ] Idle session is reaped automatically when threshold is exceeded
- [ ] Reaping a session uses the same `SessionStopConsumer` path as explicit DELETE — no duplicate teardown logic

---

## T-22 — UI: Session API client

**Title:** `UI — sessionApi.ts: typed fetch wrappers for POST / GET / DELETE sessions`
**Area:** UI / Services
**Estimated Hours:** 2 h
**Dependencies:** T-16

### Description

Create a typed API client module (e.g. `src/api/sessionApi.ts`) that wraps all session-related HTTP calls. This is the single place in the React app that knows the API base URL, sets headers (`X-User-Id`, `Content-Type`), and maps responses to typed interfaces. All other UI code calls these functions — no raw `fetch` in components.

Functions to implement:

```ts
// Launch a new session (or return existing if already Starting/Running)
launchSession(workspaceId: string, applicationId: string): Promise<SessionDto>

// Poll a single session's current status
getSession(workspaceId: string, sessionId: string): Promise<SessionDto>

// List all sessions for the current user in the workspace
listSessions(workspaceId: string): Promise<SessionDto[]>

// Stop a running session
stopSession(workspaceId: string, sessionId: string): Promise<void>
```

`SessionDto` interface:
```ts
interface SessionDto {
  id: string
  workspaceId: string
  applicationId: string
  status: 'Pending' | 'Starting' | 'Running' | 'Stopping' | 'Stopped' | 'Failed'
  accessUrl: string | null
  createdAtUtc: string
  startedAtUtc: string | null
}
```

Also create `src/api/applicationApi.ts` with `listApplications(workspaceId: string): Promise<ApplicationDto[]>` for loading the app catalogue.

### Acceptance Criteria

- [ ] `launchSession` sends `POST /api/workspaces/{workspaceId}/sessions` with `{ applicationId }` body and returns the `SessionDto` from the `202` response body
- [ ] `getSession` sends `GET /api/workspaces/{workspaceId}/sessions/{sessionId}` and returns the current `SessionDto`
- [ ] `stopSession` sends `DELETE /api/workspaces/{workspaceId}/sessions/{sessionId}` and resolves on `202`/`204`
- [ ] Non-2xx responses throw a typed error with the response message
- [ ] All functions accept an optional `AbortSignal` for cancellation (used by the poller hook)
- [ ] No raw `fetch` calls outside this module

---

## T-23 — UI: useSessionPoller hook

**Title:** `UI — useSessionPoller hook: polls GET /sessions/{id} at interval until terminal state`
**Area:** UI / Hooks
**Estimated Hours:** 3 h
**Dependencies:** T-22

### Description

Create a custom React hook `useSessionPoller(workspaceId, sessionId, intervalMs?)` that polls `getSession` at a fixed interval and returns the latest `SessionDto`. The hook must stop polling automatically when the session reaches a terminal state (`Running`, `Failed`, `Stopped`) and must clean up the interval on unmount.

```ts
function useSessionPoller(
  workspaceId: string,
  sessionId: string | null,   // null = polling inactive
  intervalMs?: number          // default 5000 ms
): {
  session: SessionDto | null
  error: string | null
}
```

Behaviour:
- When `sessionId` is `null`, polling does not start — the hook is dormant
- On each tick, calls `getSession`; updates returned `session` state
- Stops automatically when `status` is `Running`, `Failed`, or `Stopped`
- On unmount or when `sessionId` changes to `null`, clears the interval and aborts any in-flight fetch

### Acceptance Criteria

- [ ] Polling starts when `sessionId` becomes non-null and stops when status reaches a terminal state
- [ ] Hook cleans up interval and in-flight fetch on unmount — no memory leaks, no state updates on unmounted components
- [ ] Default poll interval is 5 seconds; caller can override
- [ ] `error` field is set on network failure; polling continues (transient failures should not stop the loop)
- [ ] Passing `null` as `sessionId` stops polling without error

---

## T-24 — UI: AppCard component

**Title:** `UI — AppCard component: application icon with session status overlay`
**Area:** UI / Components
**Estimated Hours:** 4 h
**Dependencies:** T-22, T-23

### Description

Create a `AppCard` component that represents a single launchable application in the workspace gallery. The card renders the application name, type icon (Jupyter / RStudio / Custom), and a status overlay that changes based on the current session state for that user.

**Visual states:**

| State | Appearance |
|---|---|
| **Idle** | App icon, app name, "Launch" button |
| **Launching** | Spinner overlay, "Requesting…" label, click disabled |
| **Starting** | Spinner overlay, "Starting… (may take a few minutes)" label, click disabled |
| **Running** | Green active badge, "Open" button (opens `accessUrl` in new tab), "Stop" icon button |
| **Stopping** | Spinner overlay, "Stopping…" label |
| **Failed** | Red error badge, error message, "Retry" button |

Props:
```ts
interface AppCardProps {
  application: ApplicationDto
  session: SessionDto | null       // null = no active session
  onLaunch: () => void             // called when user clicks Launch or Retry
  onStop: () => void               // called when user clicks Stop
}
```

The component does not own the session state or make API calls — it is purely presentational. State and API calls are managed by the parent (T-25, T-26).

### Acceptance Criteria

- [ ] All 6 visual states render correctly with correct labels and button availability
- [ ] "Launch" / "Retry" click calls `onLaunch` — does not call API directly
- [ ] "Stop" click calls `onStop` — does not call API directly
- [ ] "Open" button opens `accessUrl` in a new browser tab (`target="_blank" rel="noopener noreferrer"`)
- [ ] While in `Launching` or `Starting` state, clicking the card/button a second time has no effect
- [ ] Component is accessible: buttons have `aria-label`, spinner has `role="status"` with screen-reader text

---

## T-25 — UI: Workspace applications page

**Title:** `UI — Workspace applications page: gallery of AppCards, load existing sessions on mount`
**Area:** UI / Pages
**Estimated Hours:** 3 h
**Dependencies:** T-22, T-24

### Description

Create (or update) the workspace detail page to show the application gallery. On mount, the page loads both the application catalogue (`listApplications`) and the user's existing active sessions (`listSessions`) and combines them so that any already-Running or already-Starting session is reflected in the correct `AppCard` state immediately — the researcher does not see stale "Idle" icons when they return to a page after launching earlier.

Session-to-application matching: match `session.applicationId` → `application.id`.

Page state shape (per application):
```ts
{
  application: ApplicationDto
  session: SessionDto | null
  pollerSessionId: string | null   // drives useSessionPoller for that card
}
```

The page renders a responsive grid of `AppCard` components and passes the correct `session` and handlers down.

### Acceptance Criteria

- [ ] On page load, `listApplications` and `listSessions` are called in parallel
- [ ] Applications that already have an active (`Starting` or `Running`) session show the correct state immediately — no flash of "Idle" state
- [ ] Applications with no active session show the `Idle` state
- [ ] Page handles empty application catalogue gracefully (e.g. "No applications configured for this workspace")
- [ ] Page handles API load errors gracefully with a user-readable message and retry option

---

## T-26 — UI: Session launch flow

**Title:** `UI — Session launch flow: click handler POST → Starting state → poller → Running / Failed`
**Area:** UI / Logic
**Estimated Hours:** 3 h
**Dependencies:** T-22, T-23, T-24, T-25

### Description

Implement the `onLaunch` handler on the workspace applications page. This is the central state machine that drives an app card from Idle to Running (or Failed).

**Flow:**

```
User clicks Launch
  │
  ▼
State → Launching
  │
  ├── call launchSession(workspaceId, applicationId)
  │       ↓ 202, { status: "Starting", id: sessionId }
  │
  ▼
State → Starting  (session.id stored, useSessionPoller activated)
  │
  ├── poller ticks every 5 s → getSession(workspaceId, sessionId)
  │       ↓ status = "Running"
  │
  ▼
State → Running   (poller stops, accessUrl available, Open button shown)
  │
  └── if status = "Failed"
        State → Failed  (poller stops, Retry button shown)
```

**Idempotency:** if `launchSession` returns a session already in `Starting` or `Running` state (API idempotency), skip the `Launching` transition and set state directly from the returned session.

**Error handling:** if the `launchSession` POST itself fails (network error, 400, 500), transition to `Failed` with the error message — do not leave the card stuck in `Launching`.

### Acceptance Criteria

- [ ] Clicking Launch transitions the card to `Launching` immediately (before API responds)
- [ ] Card transitions to `Starting` as soon as the `202` response is received (~< 2 s)
- [ ] Card transitions to `Running` once `useSessionPoller` reports `status = "Running"`; `accessUrl` is available for the Open button
- [ ] If the API returns an existing `Starting`/`Running` session, the card skips `Launching` and goes straight to the correct state
- [ ] A failed POST transitions the card to `Failed` with the error message; `Launching` state is never permanent
- [ ] No more than one `launchSession` call is made per app while a session is in-flight (button disabled during `Launching` and `Starting`)

---

## T-27 — UI: Active sessions panel

**Title:** `UI — Active sessions panel: list of Running sessions with Open and Stop actions`
**Area:** UI / Components
**Estimated Hours:** 3 h
**Dependencies:** T-22, T-24, T-25

### Description

Create an `ActiveSessionsPanel` component that sits on the workspace detail page alongside the application gallery. It lists all the user's sessions in non-terminal states (`Starting`, `Running`, `Stopping`) for the current workspace, providing a single at-a-glance view of everything in flight.

Each row shows: application name, application type icon, status badge, elapsed time since `createdAtUtc`, an **Open** button (enabled when `Running`), and a **Stop** button.

The panel derives its data from the same session state already held by the workspace applications page — it does not make its own API calls. Changes from the `AppCard` clicks (launch, stop) are reflected here automatically because they share the same state.

### Acceptance Criteria

- [ ] Panel renders one row per session in `Starting`, `Running`, or `Stopping` status
- [ ] Panel is empty (or hidden) when no sessions are active — no blank rows
- [ ] **Open** button is only enabled when `status = "Running"` and `accessUrl` is populated
- [ ] **Stop** button is available on `Running` sessions; disabled on `Starting` and `Stopping`
- [ ] Elapsed time updates live (e.g. "Started 3 min ago") or at minimum on page load
- [ ] Stopping a session from this panel updates the corresponding `AppCard` in the gallery to idle state

---

## T-28 — UI: Session stop flow

**Title:** `UI — Session stop flow: confirmation dialog, DELETE call, card reset to Idle`
**Area:** UI / Logic
**Estimated Hours:** 3 h
**Dependencies:** T-22, T-24, T-25, T-27

### Description

Implement the `onStop` handler on the workspace applications page. A stop is potentially destructive (any unsaved work in the session is lost), so it requires a confirmation step.

**Flow:**
```
User clicks Stop (from AppCard or ActiveSessionsPanel)
  │
  ▼
Confirmation dialog: "Stop session? Any unsaved work will be lost."
  │  [Cancel]  → dismiss, no state change
  │  [Stop]
  ▼
State → Stopping  (card shows "Stopping…" spinner)
  │
  ├── call stopSession(workspaceId, sessionId)  → 202 Accepted
  │
  ▼
State → Idle  (session cleared, AppCard resets to Launch button)
```

**Error handling:** if `stopSession` fails, revert the card to `Running` state and show an error toast/message — do not leave the card stuck in `Stopping`.

### Acceptance Criteria

- [ ] Clicking Stop opens a confirmation dialog before making any API call
- [ ] Cancelling the dialog makes no state change and no API call
- [ ] Confirming transitions the card to `Stopping` immediately
- [ ] After `DELETE` returns `202`/`204`, the card resets to `Idle` (session cleared from state)
- [ ] If `stopSession` fails, card reverts to `Running` with a visible error message
- [ ] Stopping from the `ActiveSessionsPanel` also resets the corresponding `AppCard` in the gallery
- [ ] The Stop button is not clickable while a stop is already in progress (prevents double-stop)
