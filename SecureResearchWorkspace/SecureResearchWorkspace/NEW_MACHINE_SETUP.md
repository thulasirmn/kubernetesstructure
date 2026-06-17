# Running SRW on a New / Local Machine

How to bring the **SRW.Api** up on a fresh developer machine. The API is a thin
orchestrator — it has **no local database**; it talks to shared Azure backing services
(Cosmos DB, Service Bus, Storage, AKS). So a new machine mostly needs **tooling +
credentials + a config file**, not a rebuild of the infrastructure.

---

## 1. Tooling / runtime

| Tool | Check / install |
|---|---|
| **.NET 8 SDK** | `dotnet --version` ≥ 8 |
| **Azure CLI** | `az login` (the app uses `DefaultAzureCredential` → picks up your az-login/VS credentials locally) |
| **kubectl + kubeconfig** | `az aks get-credentials -n srw-aks-dev -g srw-dev-rg` |
| **HTTPS dev cert** | `dotnet dev-certs https --trust` |

Off-cluster, the Kubernetes client uses `BuildConfigFromConfigFile()` →
`~/.kube/config` (`%USERPROFILE%\.kube\config`). The machine must be able to reach the
cluster's API server.

---

## 2. The config file (the main thing you create)

`appsettings.Development.json` is **gitignored**, so it does NOT come with the repo.
Create it from the template (or run `scripts\setup-azure.ps1`, which auto-populates it):

```powershell
Copy-Item src\SRW.Api\appsettings.Development.json.example src\SRW.Api\appsettings.Development.json
```

Fill in real values:

| Section | Key | Notes |
|---|---|---|
| `Cosmos` | `Endpoint`, `AccountKey` | account key for local dev (or leave blank + use `az login` identity) |
| `Azure` | `SubscriptionId`, `Region`, `AksClusterName`, `AksResourceGroup` | must match the real cluster / RG |
| `Azure` | `IngressDomain` | ingress public IP/domain (or your local port-forward target) |
| `ServiceBus` | `FullyQualifiedNamespace` (+ optional `ConnectionString` for local) | the 3 queue names are already defaulted |

> No migrations — `CosmosContainerProvider.InitializeAsync()` creates the `srw` database
> and containers at startup.

---

## 3. Azure access & RBAC (the identity you `az login` as)

The shared services already exist; your account just needs permission to use them:

| Resource | Role |
|---|---|
| Resource group (workspace storage accounts) | Contributor / Storage Account Contributor |
| Cosmos DB account | Cosmos DB Built-in Data Contributor (if using identity, not key) |
| Service Bus namespace | Azure Service Bus Data Owner (if using identity, not connection string) |

---

## 4. ⚠️ Gotchas specific to "another machine"

- **Data Protection key ring.** Storage-account keys are encrypted with ASP.NET Core
  Data Protection (`AddDataProtection()`), and the keys live **locally** by default
  (`%LOCALAPPDATA%\ASP.NET\DataProtection-Keys`).
  - Fresh Cosmos → fine.
  - Pointing machine B at the **same Cosmos** machine A wrote to → B **cannot decrypt**
    existing storage secrets (different key ring). To share, persist the key ring to a
    common store (Azure Blob + Key Vault).
- **`launchSettings.json` / port.** The `https://localhost:<port>` URL comes from
  `src\SRW.Api\Properties\launchSettings.json`, which is **untracked in git**. On another
  machine the port may differ (or default to 5000/5001) unless you copy that file over.

---

## 5. Run & verify

```powershell
dotnet restore
dotnet build
dotnet run --project src/SRW.Api
```

- Health check: `GET /health` → `{ "status": "ok" }`
- Swagger (Development only): `https://localhost:<port>/swagger`

---

## 6. To actually launch / open sessions (not just run the API)

- The target AKS cluster must already have the one-time setup applied — ingress-nginx,
  Azure File CSI driver, and `k8s/manifests/00-cluster-setup.yaml` RBAC. This is
  **per-cluster, not per-machine**.
- Network reachability: the machine must reach Cosmos, Service Bus, and the AKS API
  server. Storage accounts are created with **public network access disabled** +
  `Azure:AllowedSubnetIds` — a machine outside the allowed network may fail data-plane
  storage calls even when management-plane provisioning succeeds.
- To view a session in the browser, port-forward the ingress (public LB IP is typically
  unreachable from a dev machine):
  ```powershell
  kubectl port-forward -n ingress-nginx svc/ingress-nginx-controller 80:80
  ```
  Then open `http://localhost/s/<slug>/` (use the session's `AccessUrl` slug).

---

## Quick reference — how auth works

- **App user identity:** dev stub reads the `X-User-Id` (and optional `X-User-Name`)
  HTTP header (`src/SRW.Api/Auth/CurrentUser.cs`). Sessions are scoped to that value;
  Keycloak/JWT is wired but deferred. No real auth yet — the API trusts whatever header
  you send.
- **Kubernetes API:** ServiceAccount token in-cluster (`InClusterConfig()`), kubeconfig
  locally. Nothing stored in `appsettings`/DB.
- **Azure (Cosmos / Service Bus / Storage):** `DefaultAzureCredential` → Workload Identity
  on AKS, `az login` locally.

See also: `AZURE_SETUP.md` (full one-time Azure provisioning) and `README.md`.
