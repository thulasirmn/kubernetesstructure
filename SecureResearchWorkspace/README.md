# Secure Research Workspace (SRW) — .NET 8 + AKS

A multi-tenant research platform on Azure. Each **workspace** is an isolation boundary
holding shared data; **researchers** in a workspace launch Jupyter, RStudio, or custom
Docker images on demand, all reading and writing the same underlying File Share
**concurrently**.

## Solution layout

```
SecureResearchWorkspace.sln
└── src/
    ├── SRW.Domain         # Pure entities (Workspace, UserSession, Application)
    ├── SRW.Core           # Application services + abstractions (no Azure SDK refs)
    ├── SRW.Infrastructure # Azure SDK + KubernetesClient + EF Core implementations
    └── SRW.Api            # Minimal API endpoints, composition root
```

Clean dependency direction: `Api → Infrastructure → Core → Domain`. The `Core` layer
talks only to abstractions, so swapping Azure for AWS or Keycloak for Auth0 is a
single-project change.

## How a workspace is born

```
POST /api/workspaces
   │
   ▼
WorkspaceProvisioningService.CreateAsync
   ├── 1. Insert Workspace row (Pending)
   ├── 2. AzureStorageProvisioner.ProvisionAsync
   │      → Creates srw<guid> storage account
   │      → Creates "workspace-share" file share
   │      → Returns primary access key
   ├── 3. WorkspaceSecretStore.SetStorageKeyAsync   (encrypted in DB)
   ├── 4. KubernetesOrchestrator.EnsureWorkspaceNamespaceAsync
   │      → Creates ws-<name>-<id> namespace
   │      → Creates Secret 'azure-storage-creds' (the CSI driver reads this)
   │      → Creates default-deny NetworkPolicy
   └── 5. Mark Active
```

## How a researcher launches Jupyter

```
POST /api/workspaces/{id}/sessions    body: { applicationId }
   │
   ▼
SessionLauncher.LaunchAsync
   ├── 1. Fetch Workspace + Application from DB
   ├── 2. Idempotency: if user already has an active session for this app → return it
   ├── 3. AzureStorageProvisioner.EnsureUserDirectoryAsync
   │      → Creates /<userId>/ inside the workspace's file share
   │      → This is the subPath the pod mounts
   ├── 4. KubernetesOrchestrator.LaunchSessionAsync
   │      ├── Deployment   srw-api creates "sess-<slug>" with:
   │      │      - container image from app catalog
   │      │      - resource limits (CPU/RAM)
   │      │      - volumeMount: workspace-share, subPath=<userId>
   │      ├── Service     ClusterIP, port 80 → app port (8888 / 8787 / custom)
   │      └── Ingress     /s/<slug>(/|$)(.*)  →  service:80
   └── 5. Save session, return AccessUrl = https://research.example.com/s/<slug>/
```

The user opens the URL → ingress-nginx routes to the per-user pod → Jupyter responds.

## Why concurrent multi-user works

Azure File shares are SMB/NFS — they support **many concurrent readers and writers**.
Every researcher in a workspace gets their *own* pod (their own kernel, their own R
session, their own memory) but they all mount the *same* share. The CSI volume
attribute `subPath=<userId>` keeps each user's files in their own subfolder. If two
users want a common drop area, the orchestrator can also mount a second volume at
`/shared` without subPath.

Mount options (`cache=strict,nosharesock,mfsymlinks`) are tuned for safe shared use —
strict caching avoids stale-read issues across pods; `nosharesock` makes each pod's
SMB session independent so one slow client doesn't block the others.

## Auth: deferred (Keycloak-ready)

`ICurrentUser` is the boundary. Today the dev middleware reads `X-User-Id` from a
trusted gateway header. To wire Keycloak later, in `Program.cs`:

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o => {
        o.Authority = "https://keycloak.example.com/realms/research";
        o.Audience  = "srw-api";
    });

// Replace UseCurrentUser() with:
app.UseAuthentication();
app.UseAuthorization();
// And populate CurrentUser from ClaimsPrincipal in middleware.
```

No other code changes needed — every service consumes `ICurrentUser`.

## Production checklist

- [ ] Storage accounts: enable **Private Endpoint** + disable public network access (already set in code, requires the AKS subnet to be allowed)
- [ ] Per-workspace storage key → **Azure Key Vault** instead of encrypted DB column (`IWorkspaceSecretStore` is already abstracted)
- [ ] `ServiceAccount srw-api` → AKS **Workload Identity** federated to a Managed Identity with Storage Account Contributor (so we never see keys at all; the CSI driver uses the federated token)
- [ ] Add **resource quotas** + LimitRanges per namespace
- [ ] Add **PodSecurity** standards (`restricted` profile) on workspace namespaces
- [ ] Add idle-session reaper (cron service that stops sessions with `LastActivityUtc > 4h`)
- [ ] Wire **Azure Monitor** + **Container Insights**, alert on storage throttling
- [ ] Replace dev auth middleware with Keycloak OIDC
- [ ] Restrict ingress to known corporate IPs via `nginx.ingress.kubernetes.io/whitelist-source-range`

## Build & run locally

```bash
dotnet restore
dotnet ef database update --project src/SRW.Infrastructure --startup-project src/SRW.Api
dotnet run --project src/SRW.Api
```

Swagger: <http://localhost:5000/swagger>
