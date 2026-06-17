# SRW on AKS — Pre-read Q&A

Meeting-ready answers to the questions raised on moving research apps from the
**TRE / App Service Plan per workspace** model to the **Secure Research Workspace (SRW)
platform on a shared AKS cluster**.

Items are flagged **NEW** (introduced by the SRW/AKS model) vs **EXISTING** (already in
today's TRE/App Service world) where it matters.

## Context recap
The proposal moves research apps (Jupyter, RStudio, DBGate, FDSA) off the **TRE model — one
App Service Plan per workspace** onto the **SRW platform: one shared AKS cluster, pods
launched per session**. ~3 weeks effort, can start immediately, and modeled at **~71% lower
compute cost (~$114K/yr saved at 50 workspaces / 500 researchers)** because App Service Plans
bill 24×7 and can't scale to zero, while AKS bills per active session and reaps idle pods.

---

## A. Clusters & topology

**How many clusters overall?**
**One** shared AKS cluster for the whole platform in the current design. Isolation between
workspaces is done *inside* the cluster (namespaces + NetworkPolicies + per-workspace storage
accounts), **not** by giving each workspace its own cluster. A multi-cluster layout is a
future scaling lever, not the launch design (see `docs/architecture_multicluster.drawio`).

**#Workspaces per cluster**
50 in the modeled scenario. That is **not a hard limit** — it's bounded by node-pool capacity
and concurrent pods, not workspace count. The fixed per-workspace footprint is tiny
(1 namespace + 1 Secret + 1 NetworkPolicy, ~0 cost when idle), so the real ceiling is
**concurrent running sessions**, not number of workspaces.

**AKS cluster size limits / #AKS clusters per subscription**
- Up to **5,000 nodes per cluster** (with the uptime-SLA tier), default **250 pods per node**,
  soft default of ~**1,000 nodes** before extra planning.
- Default **AKS cluster quota is generous per subscription** (commonly raised via support
  request); not a constraint at this scale.

**Is there isolation of a *set of users per workspace*, or is each user isolated in their own
pod — is "#users per workspace" still a concept?**
Both, and the distinction is the key design point:
- **The workspace is still the isolation boundary** for *data and membership* — same as today.
  It has a member list, an approved dataset (its own storage account + File Share), a
  dedicated K8s namespace, and a default-deny NetworkPolicy. So "#users in a workspace"
  absolutely still exists.
- **What changed:** compute is no longer one shared app instance for all 10 users. **Each user
  now gets their own pod per app** (their own Jupyter kernel / R session / memory), all
  mounting the same workspace share under a per-user `subPath`. So you get *both* workspace-
  level data isolation **and** per-user compute isolation — stronger than the App Service
  model where everyone shared one app instance.

**Will user AKS containers be repurposed to other users after a session ends?**
**No.** Pods are **per-user and ephemeral** — created on launch, deleted on stop. A pod is
never handed to a different user. What *is* reused is the underlying **node** (the VM): the
cluster bin-packs many users' pods onto shared nodes and autoscales nodes down when empty.
That's where the cost saving comes from — shared nodes, not shared containers.

**Is there any user session state for current apps (RStudio, Jupyter)?**
Yes, but it lives **on the File Share, not in the pod**. Each user's notebooks/files/scripts
are written to their per-user `subPath` on the workspace's Azure File Share. **In-memory
state** (a live Jupyter kernel, R variables in RAM) lives only in the pod and is **lost when
the pod stops** — exactly as it is today when an App Service restarts. Persisted files
survive; live memory does not.

**What changes for apps that need session state?**
- File/disk state already survives (CSI-mounted share).
- In-memory/long-lived state: the pattern is to **persist to the mounted volume**, or for a
  genuinely stateful app, run it as a **StatefulSet with its own PVC**. The idle reaper would
  need a longer/disabled threshold for those.
- Net: no regression vs App Service for state — apps that lose memory on restart today behave
  the same; apps that persist to disk keep working because the share is mounted.

**How are approved datasets mounted onto the pod?**
Via the **Azure File CSI driver** over **SMB**. Each workspace has its own storage account +
`workspace-share`; the storage key is pushed as a K8s Secret (`azure-storage-creds`) into the
workspace namespace, and the pod mounts the share with `subPath=<userId>` for per-user
isolation. Mount options (`cache=strict,nosharesock,mfsymlinks`) are tuned for safe concurrent
multi-reader/writer use. A **shared/read-only dataset** can be mounted as a second volume
*without* subPath (e.g. at `/shared`) so all users in the workspace see the same approved data.

---

## B. Architecture write-up & Service Bus

**Can I get a write-up of this architecture?**
Yes — it already exists in the repo as **`RESOURCE_TOPOLOGY.md`** (full provisioning / launch /
stop / delete flows, the 50×10 resource breakdown, a visual layout, and node-sizing guidance).
`README.md` and `EXEC_SUMMARY.md` complement it.

**Do we have Service Bus in the arch today? (new vs existing)**
- **Service Bus is NEW** — it is part of the SRW/AKS design, not the current App Service model.
  It decouples slow Azure operations from the HTTP path. Three queues:
  `srw-workspace-provision`, `srw-session-stop`, `srw-workspace-cleanup` (dead-letter after 10
  delivery attempts).
- New vs existing split for the deck:

| Component | Status |
|---|---|
| AKS cluster + node pools | **NEW** |
| ingress-nginx, NetworkPolicies, per-workspace namespaces | **NEW** |
| Service Bus (3 queues) | **NEW** |
| Cosmos DB | shared/**existing** prod infra (excluded from cost delta) |
| Azure Files per-workspace (datasets) | **EXISTING** concept, carried forward |
| App Service Plans | **being retired** |

---

## C. Disadvantages & idle handling

**Disadvantages of AKS vs App Service Plans**
- **Operational complexity** — you now own a Kubernetes cluster (upgrades, node pools, CSI,
  ingress) vs a PaaS you don't manage.
- **Cold start** — launching a session may need a node to scale up (seconds–minutes) vs an
  always-warm plan.
- **Networking/ingress** is your responsibility (nginx, TLS, NetworkPolicy) vs built-in on
  App Service.
- **Eventual consistency** — async provisioning means a workspace is `Pending` briefly; needs
  status polling in the UX.
- **Cost discipline required** — savings depend on autoscaler + idle reaper actually working;
  a misconfigured "never scale down" erodes (but doesn't erase) the win — worst case still
  −45% vs TRE.
- Skills/runbook ramp-up for the team.

**Do we detect session end to reuse the pod? Any idle monitoring?**
- **Explicit stop:** `DELETE …/sessions/{id}` → queues a stop → consumer tears down the
  pod/service/ingress.
- **Idle stop:** `IdleSessionReaper` runs **every 10 minutes** and stops any session idle past
  the **8-hour threshold** (configurable). Pods are deleted, not reused.
- **Known gap to flag:** today the idle clock keys off `LastActivityUtc`, which is currently
  **stamped at session creation and not yet updated by a live activity heartbeat**. So as
  written, the reaper effectively stops sessions ~8h after *launch*, not after *last real
  interaction*. Wiring a real activity signal (ingress request timestamp or app-level ping) is
  a small, known follow-up — listed as a work item, not claimed as done.

---

## D. Monitoring & scaling

**How are things monitored and scaled?**
- **Scaling:** AKS **Cluster Autoscaler** adds nodes when sessions launch and removes empty
  nodes when they're reaped. Per-app pods are created on demand. (Per-pod HPA isn't needed —
  each session is one user's pod; we scale the *node pool*, not replicas.)
- **Monitoring (today):** the platform tracks session lifecycle itself — `SessionStatusPoller`
  (every 15s) reconciles K8s ready-replicas into session status; background jobs log
  provisioning / reaping / orphan-cleanup.
- **Monitoring (planned, on the checklist):** **Azure Monitor + Container Insights** for
  cluster/node/pod metrics and logs, with alerts on storage throttling. Wired as a production
  to-do, not yet enabled.

**Elaborate on "centralized" monitoring**
In the TRE model each App Service Plan is its own silo — 50 separate things to watch. On AKS,
**one cluster emits all telemetry to a single Azure Monitor / Log Analytics workspace** — every
workspace's pods, nodes, ingress, and the API's own logs land in one place with consistent
labels (`srw.io/workspace-id`). That means one dashboard, one alerting setup, and
cross-workspace queries (e.g. "top CPU sessions across all workspaces") instead of 50
disconnected plan blades. That single-pane-of-glass is what "centralized" refers to.

---

## E. Workspace ↔ pods relationship

**Is anything still tied to the concept of a set of pods belonging to a workspace?**
**Yes — the Kubernetes namespace is that binding.** Every session pod for a workspace lives in
its `ws-<name>-<id>` namespace, alongside that workspace's storage Secret and its default-deny
NetworkPolicy. So a workspace = {namespace + all its session pods + its Secret + its
NetworkPolicy + its storage account/share}. Deleting the workspace deletes the namespace, which
cascades all those pods. The grouping is preserved; it just moved from "one App Service Plan"
to "one namespace."

---

## F. Self-healing

**How will the system self-heal?**
1. **Kubernetes reconciliation** — a Deployment continuously ensures its pod is running; if a
   pod crashes or a node dies, K8s reschedules it automatically.
2. **Liveness/readiness probes** — the API exposes `/health` on 8080; unhealthy pods are
   restarted / pulled from rotation.
3. **Message retry + dead-letter** — failed provisioning/stop/cleanup messages are requeued
   (up to 10 attempts) then dead-lettered for investigation, so a transient Azure error
   doesn't lose the operation.
4. **Orphan reconciliation** — `OrphanResourceCleaner` runs every 24h, finds namespaces whose
   workspace is no longer active in Cosmos, and removes them — self-correcting drift between
   desired and actual state.
5. **Idle reaper** — reclaims abandoned sessions automatically.
6. **Cluster autoscaler** — replaces/repacks capacity as nodes come and go.

None of this exists in the App Service model — there a crashed app just stays down until
someone notices.

---

## G. Benefits — current/specific vs generic

**Rolling deployments / reduced downtime — do we get this for new RStudio/Jupyter versions?**
- **Generically, yes:** K8s Deployments support rolling updates and the platform pulls each
  app's image from a catalog, so rolling out a new Jupyter/RStudio image is a controlled,
  zero-downtime-capable change — far better than rebuilding an App Service.
- **Specific nuance to be honest about:** a **running session pod is pinned to the image it
  launched with**. A new version applies to **newly launched sessions**; in-flight researchers
  keep their current version until they restart their session. So "rolling deployment with
  reduced downtime" is real for the **platform/API and for new sessions**, but you don't
  hot-swap the runtime under a live notebook (nor would you want to).
- **Concrete current benefits** beyond the generic ones: per-user/per-app isolation, more
  compute per active researcher (0.5→2 vCPU burst vs ~0.4 vCPU shared), workspaces free when
  idle, self-healing, and centralized monitoring — all on top of the ~71% cost reduction.

---

## H. Mounting additional datasets from a Blob container (feasibility)

**Today Jupyter and RStudio mount the Azure File Share. If a user requests to mount additional
datasets that live in a Blob container, is that possible?**

**Yes — fully possible, and it slots in next to the existing mount.** The current mount uses
the **Azure File CSI driver** (`file.csi.azure.com`, SMB) for `workspace-share`. To mount a
**blob container**, add a *second* volume using the **Azure Blob CSI driver**
(`blob.csi.azure.com`). Both drivers coexist in the same pod — the user gets the file share at
one path and the blob dataset at another. Nothing about the existing share changes.

**The one decision — how the blob is exposed as a filesystem:**

| Mode | `protocol` | Best for | Caveats |
|---|---|---|---|
| **blobfuse2** (FUSE) | `fuse` | Large, **read-mostly** approved datasets (the common research case) | Not fully POSIX — weak on random writes, rename, file locking. Mount **read-only** and it's excellent. |
| **NFS 3.0** | `nfs` | Higher throughput, more filesystem-like behavior | Requires a **Premium block-blob** account + the container be NFS-enabled, and VNet/private-endpoint access (no key auth, no in-transit encryption by default). |

For "approved datasets the researcher just reads," **blobfuse2 mounted read-only** is the
natural fit and the safest (read-only prevents accidental dataset mutation).

**What it takes to enable:**
1. **One-time on the cluster** — turn on the blob driver, same way the file driver was enabled:
   ```bash
   az aks update -n srw-aks -g srw-platform-rg --enable-blob-driver
   ```
2. **Credentials** — a K8s Secret in the workspace namespace with the dataset storage account
   name + key (or, better, Workload Identity so no key is handled — matches the production
   checklist direction).
3. **Code** — add a second `V1Volume` + `V1VolumeMount`. Alongside the existing
   `workspace-share` volume:
   ```csharp
   // Additional read-only dataset from a blob container
   var datasetVolume = new V1Volume
   {
       Name = "approved-dataset",
       Csi = new V1CSIVolumeSource
       {
           Driver = "blob.csi.azure.com",
           ReadOnlyProperty = true,                 // approved datasets are read-only
           VolumeAttributes = new Dictionary<string, string>
           {
               ["secretName"]    = "dataset-blob-creds",   // account+key for the dataset SA
               ["containerName"] = dataset.ContainerName,
               ["protocol"]      = "fuse",                 // blobfuse2
               ["mountOptions"]  = "-o allow_other -o ro --file-cache-timeout-in-seconds=120"
           }
       }
   };
   ```
   and the matching mount on the container:
   ```csharp
   new V1VolumeMount { Name = "approved-dataset", MountPath = "/data/<dataset-name>", ReadOnlyProperty = true }
   ```

**How to make it "user-requestable" cleanly:**
The volume is currently hardcoded to one share. To support *"a user requests additional
datasets,"* the clean change (consistent with the Clean Architecture) is to give the
**Workspace** (or the launch request) a list of approved dataset mounts — e.g.
`WorkspaceDataset { ContainerName, AccountName, MountPath, ReadOnly }` — and have
`LaunchSessionAsync` loop over them to build N volumes/mounts instead of the single hardcoded
one. The dataset list should be governed (approved per workspace), not free-form per session,
to preserve the "approved datasets" control that exists today.

**Two things to flag:**
- **Governance:** mounting a blob container is a data-access decision. Keep it tied to
  *workspace-level approval* (the same boundary that owns the file share + NetworkPolicy), so a
  user can't self-mount arbitrary containers. This keeps the isolation story intact.
- **blobfuse ≠ a real filesystem:** if researchers expect to *write* into the dataset, or the
  app does locking/random writes, blobfuse will disappoint — use the file share (current
  approach) for read-write working data and blob for read-only reference datasets. That
  read-write working set / read-only reference data split is the recommended pattern.

> **Status: feasibility confirmed, not yet implemented.** No code change has been made — this
> section scopes what the change would involve.

---

## Known gaps to surface proactively
1. **Live idle-activity tracking isn't wired yet** — the reaper currently counts from session
   launch, not last interaction.
2. **Azure Monitor / Container Insights is on the production checklist, not yet enabled.**
3. **Blob-container dataset mounts** (Section H) are feasibility-confirmed but not implemented.
