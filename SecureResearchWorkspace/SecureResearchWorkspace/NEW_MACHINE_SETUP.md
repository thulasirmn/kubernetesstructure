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
| **Azure CLI** | `az login` (the app uses `DefaultAzureCredential` → picks up your az-login credentials locally) |
| **kubectl + kubeconfig** | `az aks get-credentials -n srw-aks-dev -g srw-dev-rg` |
| **Helm CLI** | `helm version` ≥ 3 — [install](https://helm.sh/docs/intro/install/) |
| **HTTPS dev cert** | `dotnet dev-certs https --trust` |

### How Kubernetes config is resolved (three-way)

The API resolves its K8s client config at startup in this order:

1. **Inside a K8s pod** — uses in-cluster service account token automatically
2. **Local dev** — uses `~/.kube/config` (`%USERPROFILE%\.kube\config`) populated by `az aks get-credentials`
3. **App Service / no kubeconfig** — fetches credentials from AKS via ARM API using Managed Identity (`Azure:AksClusterName` + `Azure:AksResourceGroup` must be set)

For local dev, step 2 applies. No extra config is needed beyond running `az aks get-credentials`.

---

## 2. The config file

`appsettings.Development.json` is **gitignored**, so it does NOT come with the repo.
Create it from the template:

```powershell
Copy-Item src\SRW.Api\appsettings.Development.json.example src\SRW.Api\appsettings.Development.json
```

Fill in real values:

| Section | Key | Notes |
|---|---|---|
| `Cosmos` | `Endpoint`, `AccountKey` | Account key for local dev (or leave blank + use `az login` identity) |
| `Azure` | `SubscriptionId` | Azure subscription where workspaces are created |
| `Azure` | `Region`, `AksClusterName`, `AksResourceGroup` | Must match the real cluster / RG |
| `Azure` | `IngressDomain` | Ingress public IP or domain (or raw IP for testing) |
| `ServiceBus` | `FullyQualifiedNamespace` (+ optional `ConnectionString` for local) | The 3 queue names are already defaulted |
| `Helm` | `HelmBinaryPath` | Path to `helm` binary; `"helm"` if on PATH |
| `Helm` | `SessionChartPath` | **Absolute path** to `charts/session` in your local clone |

> `dotnet run --project src/SRW.Api` sets the working directory to `src/SRW.Api`, so the
> default relative `SessionChartPath` of `"charts/session"` resolves incorrectly. Use the
> full absolute path, e.g. `"C:/dev/SecureResearchWorkspace/SecureResearchWorkspace/charts/session"`.

> No migrations — `CosmosContainerProvider.InitializeAsync()` creates the `srw` database
> and containers at startup.

See **[NEW_MACHINE_SETUP_HELM.md](NEW_MACHINE_SETUP_HELM.md)** for the full `appsettings.Development.json` template.

---

## 3. Azure access & RBAC

The shared services already exist; your account just needs permission to use them:

| Resource | Role |
|---|---|
| Resource group (workspace storage accounts) | Contributor / Storage Account Contributor |
| Cosmos DB account | Cosmos DB Built-in Data Contributor (if using identity, not key) |
| Service Bus namespace | Azure Service Bus Data Owner (if using identity, not connection string) |

---

## 4. Gotchas

- **Data Protection key ring.** Storage-account keys are encrypted with ASP.NET Core
  Data Protection, and the keys live **locally** by default
  (`%LOCALAPPDATA%\ASP.NET\DataProtection-Keys`).
  - Fresh Cosmos → fine.
  - Pointing machine B at the **same Cosmos** machine A wrote to → B **cannot decrypt**
    existing storage secrets (different key ring). To share, persist the key ring to a
    common store (Azure Blob + Key Vault).
- **`launchSettings.json` / port.** The `https://localhost:<port>` URL comes from
  `src\SRW.Api\Properties\launchSettings.json`, which is **untracked in git**. On another
  machine the port may differ (or default to 5000/5001) unless you copy that file.
- **`Helm:SessionChartPath` must be absolute** when running locally with `dotnet run`.
  See section 2 above.
- **`resourceGroup` is required** in the `POST /api/workspaces` request body — this is
  the Azure resource group where the workspace's storage account will be created.

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

## 6. To launch sessions (not just run the API)

- The target AKS cluster must already have the one-time setup applied — ingress-nginx,
  Azure File CSI driver, and `k8s/manifests/00-cluster-setup.yaml` RBAC. See
  **[AZURE_SETUP.md](AZURE_SETUP.md)** for the full provisioning steps.
- Helm must be installed and reachable at `Helm:HelmBinaryPath`. The `charts/session/`
  directory must be accessible at `Helm:SessionChartPath`.
- Network reachability: the machine must reach Cosmos, Service Bus, and the AKS API server.
- To view a session in the browser, port-forward the ingress (public LB IP is typically
  unreachable from a dev machine):
  ```powershell
  kubectl port-forward -n ingress-nginx svc/ingress-nginx-controller 80:80
  ```
  Then open `http://localhost/s/<slug>/` (use the session's `AccessUrl` slug).

---

## 7. Auth quick reference

- **App user identity:** dev stub reads the `X-User-Id` (and optional `X-User-Name`)
  HTTP header (`src/SRW.Api/Auth/CurrentUser.cs`). Keycloak/JWT is wired but deferred.
  No real auth — the API trusts whatever header you send.
- **Kubernetes API:** in-cluster service account token (AKS), kubeconfig locally,
  or ARM-fetched credentials (App Service). Nothing stored in `appsettings`.
- **Azure (Cosmos / Service Bus / Storage):** `DefaultAzureCredential` → Workload Identity
  on AKS, `az login` locally.

See also: `AZURE_SETUP.md` (full one-time Azure provisioning) and `README.md`.
