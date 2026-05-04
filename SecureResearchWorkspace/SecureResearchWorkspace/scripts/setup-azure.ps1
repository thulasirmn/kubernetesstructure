# =============================================================================
# Secure Research Workspace — Azure Infrastructure Setup (PowerShell)
# =============================================================================
# Run from the repo root:
#   Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
#   .\scripts\setup-azure.ps1
# =============================================================================

#Requires -Version 5.1

# ── Configuration — edit these before running ─────────────────────────────────
$LOCATION       = "eastus"
$RG             = "srw-dev-rg"
$COSMOS_ACCOUNT = "srw-cosmos-dev"
$SB_NAMESPACE   = "srw-servicebus-dev"
$AKS_CLUSTER    = "srw-aks-dev"
$ACR_NAME       = "srwregistrydev"       # globally unique, lowercase, alphanumeric
$IDENTITY_NAME  = "srw-api-identity"
$AKS_NODE_SIZE  = "Standard_D4s_v3"
$AKS_MIN_NODES  = 2
$AKS_MAX_NODES  = 10
$K8S_NAMESPACE  = "srw-platform"
$K8S_SA_NAME    = "srw-api"

$APPSETTINGS    = "src\SRW.Api\appsettings.Development.json"
$MANIFEST_API   = "k8s\manifests\10-api-deployment.yaml"
$MANIFEST_SETUP = "k8s\manifests\00-cluster-setup.yaml"

# ── Helpers ───────────────────────────────────────────────────────────────────
function Write-Step   { param($n, $msg) Write-Host "`n[STEP $n] $msg" -ForegroundColor Cyan }
function Write-Ok     { param($msg)     Write-Host "  [OK]   $msg"   -ForegroundColor Green }
function Write-Info   { param($msg)     Write-Host "  [-->]  $msg"   -ForegroundColor White }
function Write-Warn   { param($msg)     Write-Host "  [WARN] $msg"   -ForegroundColor Yellow }
function Write-Fail   { param($msg)     Write-Host "  [ERR]  $msg"   -ForegroundColor Red; exit 1 }
function Write-Hr     { Write-Host ("─" * 60) -ForegroundColor Cyan }

function Invoke-Az {
    param([string[]]$Arguments)
    $output = & az @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "az $($Arguments -join ' ') failed: $output"
    }
    return $output
}

function Resource-Exists {
    param([string[]]$ShowArgs)
    & az @ShowArgs 2>$null | Out-Null
    return $LASTEXITCODE -eq 0
}

# ── Step 0: Prerequisites ─────────────────────────────────────────────────────
Write-Step 0 "Checking prerequisites"

foreach ($tool in @("az", "kubectl", "helm")) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        Write-Fail "'$tool' not found in PATH. Install it and re-run."
    }
    Write-Ok "$tool found"
}

# ── Step 1: Azure Login ───────────────────────────────────────────────────────
Write-Step 1 "Verifying Azure login"

$accountJson = & az account show 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Info "Not logged in — launching az login..."
    Invoke-Az @("login") | Out-Null
}

$account        = & az account show | ConvertFrom-Json
$SUBSCRIPTION_ID = $account.id
$ACCOUNT_NAME   = $account.name
Write-Ok "Subscription: $ACCOUNT_NAME ($SUBSCRIPTION_ID)"

# ── Step 2: Resource Group ────────────────────────────────────────────────────
Write-Step 2 "Resource Group"

if (Resource-Exists @("group", "show", "--name", $RG)) {
    Write-Warn "Resource group '$RG' already exists — skipping"
} else {
    Invoke-Az @("group", "create", "--name", $RG, "--location", $LOCATION, "--output", "none")
    Write-Ok "Created resource group: $RG ($LOCATION)"
}

# ── Step 3: Cosmos DB ─────────────────────────────────────────────────────────
Write-Step 3 "Azure Cosmos DB (NoSQL)"

if (Resource-Exists @("cosmosdb", "show", "--name", $COSMOS_ACCOUNT, "--resource-group", $RG)) {
    Write-Warn "Cosmos DB '$COSMOS_ACCOUNT' already exists — skipping"
} else {
    Write-Info "Creating Cosmos DB account (this takes ~2 minutes)..."
    Invoke-Az @(
        "cosmosdb", "create",
        "--name", $COSMOS_ACCOUNT,
        "--resource-group", $RG,
        "--kind", "GlobalDocumentDB",
        "--capabilities", "EnableServerless",
        "--default-consistency-level", "Session",
        "--locations", "regionName=$LOCATION",
        "--output", "none"
    )
    Write-Ok "Created Cosmos DB account: $COSMOS_ACCOUNT"
}

$COSMOS_ENDPOINT = (Invoke-Az @("cosmosdb", "show", "--name", $COSMOS_ACCOUNT, "--resource-group", $RG, "--query", "documentEndpoint", "-o", "tsv")).Trim()
$COSMOS_KEY      = (Invoke-Az @("cosmosdb", "keys", "list", "--name", $COSMOS_ACCOUNT, "--resource-group", $RG, "--query", "primaryMasterKey", "-o", "tsv")).Trim()

Write-Ok "Cosmos endpoint : $COSMOS_ENDPOINT"
Write-Ok "Cosmos key      : $($COSMOS_KEY.Substring(0,8))... (truncated)"

# ── Step 4: Service Bus ───────────────────────────────────────────────────────
Write-Step 4 "Azure Service Bus + Queues"

if (Resource-Exists @("servicebus", "namespace", "show", "--name", $SB_NAMESPACE, "--resource-group", $RG)) {
    Write-Warn "Service Bus '$SB_NAMESPACE' already exists — skipping"
} else {
    Invoke-Az @(
        "servicebus", "namespace", "create",
        "--name", $SB_NAMESPACE,
        "--resource-group", $RG,
        "--sku", "Standard",
        "--location", $LOCATION,
        "--output", "none"
    )
    Write-Ok "Created Service Bus namespace: $SB_NAMESPACE"
}

$SB_FQNS = "$SB_NAMESPACE.servicebus.windows.net"

function Create-Queue {
    param($QueueName, $LockDuration)
    if (Resource-Exists @("servicebus", "queue", "show", "--name", $QueueName, "--namespace-name", $SB_NAMESPACE, "--resource-group", $RG)) {
        Write-Warn "Queue '$QueueName' already exists — skipping"
    } else {
        Invoke-Az @(
            "servicebus", "queue", "create",
            "--name", $QueueName,
            "--namespace-name", $SB_NAMESPACE,
            "--resource-group", $RG,
            "--max-delivery-count", "10",
            "--lock-duration", $LockDuration,
            "--default-message-time-to-live", "P14D",
            "--output", "none"
        )
        Write-Ok "Created queue: $QueueName"
    }
}

Create-Queue "srw-workspace-provision" "PT5M"
Create-Queue "srw-session-stop"        "PT5M"
Create-Queue "srw-workspace-cleanup"   "PT5M"

# ── Step 5: Container Registry ────────────────────────────────────────────────
Write-Step 5 "Azure Container Registry"

if (Resource-Exists @("acr", "show", "--name", $ACR_NAME, "--resource-group", $RG)) {
    Write-Warn "ACR '$ACR_NAME' already exists — skipping"
} else {
    Invoke-Az @(
        "acr", "create",
        "--name", $ACR_NAME,
        "--resource-group", $RG,
        "--sku", "Basic",
        "--location", $LOCATION,
        "--output", "none"
    )
    Write-Ok "Created ACR: $ACR_NAME"
}

$ACR_LOGIN_SERVER = (Invoke-Az @("acr", "show", "--name", $ACR_NAME, "--resource-group", $RG, "--query", "loginServer", "-o", "tsv")).Trim()
Write-Ok "ACR login server: $ACR_LOGIN_SERVER"

# ── Step 6: AKS Cluster ───────────────────────────────────────────────────────
Write-Step 6 "AKS Cluster (this takes 8-10 minutes)"

if (Resource-Exists @("aks", "show", "--name", $AKS_CLUSTER, "--resource-group", $RG)) {
    Write-Warn "AKS cluster '$AKS_CLUSTER' already exists — skipping"
} else {
    Write-Info "Creating AKS cluster (node size: $AKS_NODE_SIZE)..."
    Invoke-Az @(
        "aks", "create",
        "--name", $AKS_CLUSTER,
        "--resource-group", $RG,
        "--location", $LOCATION,
        "--node-count", $AKS_MIN_NODES,
        "--min-count", $AKS_MIN_NODES,
        "--max-count", $AKS_MAX_NODES,
        "--enable-cluster-autoscaler",
        "--node-vm-size", $AKS_NODE_SIZE,
        "--enable-oidc-issuer",
        "--enable-workload-identity",
        "--enable-managed-identity",
        "--attach-acr", $ACR_NAME,
        "--network-plugin", "azure",
        "--generate-ssh-keys",
        "--output", "none"
    )
    Write-Ok "AKS cluster created: $AKS_CLUSTER"
}

Write-Info "Enabling Azure File CSI driver..."
Invoke-Az @("aks", "update", "--name", $AKS_CLUSTER, "--resource-group", $RG, "--enable-file-driver", "--output", "none")
Write-Ok "Azure File CSI driver enabled"

Write-Info "Fetching kubeconfig..."
Invoke-Az @("aks", "get-credentials", "--name", $AKS_CLUSTER, "--resource-group", $RG, "--overwrite-existing")
Write-Ok "kubectl context set to: $AKS_CLUSTER"

$AKS_OIDC_ISSUER = (Invoke-Az @("aks", "show", "--name", $AKS_CLUSTER, "--resource-group", $RG, "--query", "oidcIssuerProfile.issuerUrl", "-o", "tsv")).Trim()
Write-Ok "OIDC issuer: $AKS_OIDC_ISSUER"

# ── Step 7: Managed Identity ──────────────────────────────────────────────────
Write-Step 7 "User-Assigned Managed Identity"

if (Resource-Exists @("identity", "show", "--name", $IDENTITY_NAME, "--resource-group", $RG)) {
    Write-Warn "Managed identity '$IDENTITY_NAME' already exists — skipping"
} else {
    Invoke-Az @(
        "identity", "create",
        "--name", $IDENTITY_NAME,
        "--resource-group", $RG,
        "--location", $LOCATION,
        "--output", "none"
    )
    Write-Ok "Created managed identity: $IDENTITY_NAME"
}

$IDENTITY_CLIENT_ID = (Invoke-Az @("identity", "show", "--name", $IDENTITY_NAME, "--resource-group", $RG, "--query", "clientId", "-o", "tsv")).Trim()
$IDENTITY_OBJECT_ID = (Invoke-Az @("identity", "show", "--name", $IDENTITY_NAME, "--resource-group", $RG, "--query", "principalId", "-o", "tsv")).Trim()
Write-Ok "Identity client ID : $IDENTITY_CLIENT_ID"
Write-Ok "Identity object ID : $IDENTITY_OBJECT_ID"

# ── Step 8: RBAC — Managed Identity ──────────────────────────────────────────
Write-Step 8 "RBAC — Managed Identity role assignments"

$SCOPE_RG     = "/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RG"
$SCOPE_SB     = (Invoke-Az @("servicebus", "namespace", "show", "--name", $SB_NAMESPACE, "--resource-group", $RG, "--query", "id", "-o", "tsv")).Trim()
$SCOPE_COSMOS = (Invoke-Az @("cosmosdb", "show", "--name", $COSMOS_ACCOUNT, "--resource-group", $RG, "--query", "id", "-o", "tsv")).Trim()

function Assign-Role {
    param($Role, $ObjectId, $PrincipalType, $Scope, $Label)
    $existing = & az role assignment list --role $Role --assignee $ObjectId --scope $Scope --query "[0].id" -o tsv 2>$null
    if ($existing) {
        Write-Warn "Role '$Role' on $Label already assigned — skipping"
    } else {
        Invoke-Az @(
            "role", "assignment", "create",
            "--role", $Role,
            "--assignee-object-id", $ObjectId,
            "--assignee-principal-type", $PrincipalType,
            "--scope", $Scope,
            "--output", "none"
        )
        Write-Ok "Assigned '$Role' on $Label"
    }
}

Assign-Role "Storage Account Contributor"  $IDENTITY_OBJECT_ID "ServicePrincipal" $SCOPE_RG  "resource group"
Assign-Role "Azure Service Bus Data Owner" $IDENTITY_OBJECT_ID "ServicePrincipal" $SCOPE_SB  "Service Bus"

Write-Info "Assigning Cosmos DB Built-in Data Contributor to managed identity..."
$existingCosmos = & az cosmosdb sql role assignment list --account-name $COSMOS_ACCOUNT --resource-group $RG --query "[?principalId=='$IDENTITY_OBJECT_ID'] | [0].id" -o tsv 2>$null
if ($existingCosmos) {
    Write-Warn "Cosmos role already assigned to managed identity — skipping"
} else {
    Invoke-Az @(
        "cosmosdb", "sql", "role", "assignment", "create",
        "--account-name", $COSMOS_ACCOUNT,
        "--resource-group", $RG,
        "--role-definition-name", "Cosmos DB Built-in Data Contributor",
        "--principal-id", $IDENTITY_OBJECT_ID,
        "--scope", $SCOPE_COSMOS,
        "--output", "none"
    )
    Write-Ok "Assigned Cosmos DB Built-in Data Contributor to managed identity"
}

# ── Step 9: RBAC — Local Developer ───────────────────────────────────────────
Write-Step 9 "RBAC — Local developer role assignments"

$MY_OBJECT_ID = (& az ad signed-in-user show --query id -o tsv 2>$null)
if (-not $MY_OBJECT_ID) {
    Write-Warn "Could not get signed-in user — skipping local dev role assignments"
} else {
    $MY_OBJECT_ID = $MY_OBJECT_ID.Trim()
    Assign-Role "Storage Account Contributor"  $MY_OBJECT_ID "User" $SCOPE_RG "resource group (local dev)"
    Assign-Role "Azure Service Bus Data Owner" $MY_OBJECT_ID "User" $SCOPE_SB "Service Bus (local dev)"

    $existingCosmosMe = & az cosmosdb sql role assignment list --account-name $COSMOS_ACCOUNT --resource-group $RG --query "[?principalId=='$MY_OBJECT_ID'] | [0].id" -o tsv 2>$null
    if ($existingCosmosMe) {
        Write-Warn "Cosmos role already assigned to your identity — skipping"
    } else {
        Invoke-Az @(
            "cosmosdb", "sql", "role", "assignment", "create",
            "--account-name", $COSMOS_ACCOUNT,
            "--resource-group", $RG,
            "--role-definition-name", "Cosmos DB Built-in Data Contributor",
            "--principal-id", $MY_OBJECT_ID,
            "--scope", $SCOPE_COSMOS,
            "--output", "none"
        )
        Write-Ok "Assigned Cosmos DB Built-in Data Contributor to your identity"
    }
}

# ── Step 10: Federated Identity Credential ────────────────────────────────────
Write-Step 10 "Federated Identity Credential (Workload Identity)"

if (Resource-Exists @("identity", "federated-credential", "show", "--name", "srw-api-federated", "--identity-name", $IDENTITY_NAME, "--resource-group", $RG)) {
    Write-Warn "Federated credential already exists — skipping"
} else {
    Invoke-Az @(
        "identity", "federated-credential", "create",
        "--name", "srw-api-federated",
        "--identity-name", $IDENTITY_NAME,
        "--resource-group", $RG,
        "--issuer", $AKS_OIDC_ISSUER,
        "--subject", "system:serviceaccount:${K8S_NAMESPACE}:${K8S_SA_NAME}",
        "--audiences", "api://AzureADTokenExchange",
        "--output", "none"
    )
    Write-Ok "Federated credential created"
}

# ── Step 11: Kubernetes Manifests ─────────────────────────────────────────────
Write-Step 11 "Kubernetes namespace + RBAC"

kubectl create namespace $K8S_NAMESPACE --dry-run=client -o yaml | kubectl apply -f - | Out-Null
Write-Ok "Namespace '$K8S_NAMESPACE' ensured"

kubectl apply -f $MANIFEST_SETUP
Write-Ok "Applied $MANIFEST_SETUP"

# ── Step 12: ingress-nginx ────────────────────────────────────────────────────
Write-Step 12 "ingress-nginx (Helm)"

helm repo add ingress-nginx https://kubernetes.github.io/ingress-nginx --force-update 2>&1 | Out-Null
helm repo update 2>&1 | Out-Null

helm upgrade --install ingress-nginx ingress-nginx/ingress-nginx `
    --namespace ingress-nginx `
    --create-namespace `
    --set controller.replicaCount=2 `
    --wait `
    --timeout 5m

Write-Info "Waiting for ingress LoadBalancer IP (up to 2 minutes)..."
$INGRESS_IP = ""
for ($i = 0; $i -lt 24; $i++) {
    $INGRESS_IP = & kubectl get service ingress-nginx-controller -n ingress-nginx `
        -o jsonpath='{.status.loadBalancer.ingress[0].ip}' 2>$null
    if ($INGRESS_IP) { break }
    Start-Sleep -Seconds 5
}

if (-not $INGRESS_IP) {
    Write-Warn "LoadBalancer IP not assigned yet. Run after setup:"
    Write-Warn "  kubectl get svc ingress-nginx-controller -n ingress-nginx"
    $INGRESS_IP = "PENDING"
} else {
    Write-Ok "Ingress public IP: $INGRESS_IP"
}

# ── Step 13: Kubernetes Secret ────────────────────────────────────────────────
Write-Step 13 "Kubernetes secret: srw-api-secrets"

kubectl create secret generic srw-api-secrets `
    --namespace $K8S_NAMESPACE `
    --from-literal=cosmos-endpoint="$COSMOS_ENDPOINT" `
    --from-literal=cosmos-key="$COSMOS_KEY" `
    --from-literal=servicebus-namespace="$SB_FQNS" `
    --dry-run=client -o yaml | kubectl apply -f - | Out-Null

Write-Ok "Secret 'srw-api-secrets' applied in namespace $K8S_NAMESPACE"

# ── Step 14: Patch appsettings.Development.json ───────────────────────────────
Write-Step 14 "Patching $APPSETTINGS"

if (Test-Path $APPSETTINGS) {
    $cfg = Get-Content $APPSETTINGS -Raw | ConvertFrom-Json

    $cfg.Cosmos.Endpoint                          = $COSMOS_ENDPOINT
    $cfg.Cosmos.AccountKey                        = $COSMOS_KEY
    $cfg.Azure.AksClusterName                     = $AKS_CLUSTER
    $cfg.Azure.AksResourceGroup                   = $RG
    $cfg.Azure.Region                             = $LOCATION
    $cfg.Azure.IngressDomain                      = $INGRESS_IP
    $cfg.ServiceBus.FullyQualifiedNamespace       = $SB_FQNS

    $cfg | ConvertTo-Json -Depth 10 | Set-Content $APPSETTINGS -Encoding UTF8
    Write-Ok "$APPSETTINGS updated with real Azure values"
} else {
    Write-Warn "$APPSETTINGS not found — skipping (are you running from the repo root?)"
}

# ── Step 15: Patch 10-api-deployment.yaml ─────────────────────────────────────
Write-Step 15 "Patching $MANIFEST_API"

if (Test-Path $MANIFEST_API) {
    $yaml = Get-Content $MANIFEST_API -Raw
    $yaml = $yaml -replace "<ACR_NAME>",           $ACR_NAME
    $yaml = $yaml -replace "<IDENTITY_CLIENT_ID>", $IDENTITY_CLIENT_ID
    $yaml = $yaml -replace "research\.yourdomain\.com", $INGRESS_IP
    Set-Content $MANIFEST_API $yaml -Encoding UTF8
    Write-Ok "$MANIFEST_API patched with ACR, identity client ID, and ingress IP"
} else {
    Write-Warn "$MANIFEST_API not found — skipping"
}

# ── Summary ───────────────────────────────────────────────────────────────────
Write-Hr
Write-Host "`nAzure infrastructure provisioning complete!`n" -ForegroundColor Green

$summary = @(
    [PSCustomObject]@{ Resource="Resource Group";        Value=$RG }
    [PSCustomObject]@{ Resource="Cosmos DB";             Value=$COSMOS_ACCOUNT }
    [PSCustomObject]@{ Resource="Cosmos Endpoint";       Value=$COSMOS_ENDPOINT }
    [PSCustomObject]@{ Resource="Service Bus";           Value=$SB_FQNS }
    [PSCustomObject]@{ Resource="Container Registry";    Value=$ACR_LOGIN_SERVER }
    [PSCustomObject]@{ Resource="AKS Cluster";           Value=$AKS_CLUSTER }
    [PSCustomObject]@{ Resource="Managed Identity";      Value=$IDENTITY_NAME }
    [PSCustomObject]@{ Resource="Identity Client ID";    Value=$IDENTITY_CLIENT_ID }
    [PSCustomObject]@{ Resource="Ingress Public IP";     Value=$INGRESS_IP }
)
$summary | Format-Table -AutoSize

Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Build and push the API image:"
Write-Host "       az acr login --name $ACR_NAME"
Write-Host "       docker build -t $ACR_LOGIN_SERVER/srw-api:1.0.0 -f src/SRW.Api/Dockerfile ."
Write-Host "       docker push $ACR_LOGIN_SERVER/srw-api:1.0.0"
Write-Host ""
Write-Host "  2. Deploy to AKS:"
Write-Host "       kubectl apply -f k8s/manifests/10-api-deployment.yaml"
Write-Host "       kubectl rollout status deployment/srw-api -n srw-platform"
Write-Host ""
Write-Host "  3. Run locally against real Azure:"
Write-Host "       dotnet run --project src/SRW.Api"
Write-Hr
