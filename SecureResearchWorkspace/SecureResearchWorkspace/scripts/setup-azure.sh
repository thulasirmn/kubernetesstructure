#!/usr/bin/env bash
# =============================================================================
# Secure Research Workspace — Azure Infrastructure Setup
# =============================================================================
# Usage:
#   chmod +x scripts/setup-azure.sh
#   ./scripts/setup-azure.sh
#
# Prerequisites: az CLI, kubectl, helm
# Run from the repo root: SecureResearchWorkspace/SecureResearchWorkspace/
# =============================================================================
set -euo pipefail

# CRITICAL: Disable Git Bash / MSYS path conversion. Without this, any argument
# starting with "/" (like an Azure --scope) gets mangled into a Windows path,
# causing "MissingSubscription" errors and other obscure failures.
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*'

# ── Colours ───────────────────────────────────────────────────────────────────
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'
BLUE='\033[0;34m'; CYAN='\033[0;36m'; BOLD='\033[1m'; NC='\033[0m'

log_step()  { echo -e "\n${BLUE}${BOLD}[STEP $1]${NC} ${BOLD}$2${NC}"; }
log_ok()    { echo -e "  ${GREEN}✓${NC} $1"; }
log_info()  { echo -e "  ${CYAN}→${NC} $1"; }
log_warn()  { echo -e "  ${YELLOW}⚠${NC}  $1"; }
log_error() { echo -e "  ${RED}✗${NC} $1"; exit 1; }
hr()        { echo -e "${BLUE}────────────────────────────────────────────────────────${NC}"; }

# Helper: strip \r characters that Git Bash on Windows adds to az tsv output
aztsv() { az "$@" | tr -d '\r'; }

# ── Configuration — edit these before running ─────────────────────────────────
LOCATION="eastus"
RG="srw-dev-rg"
COSMOS_ACCOUNT="srw-cosmos-dev"
SB_NAMESPACE="srw-servicebus-dev"
AKS_CLUSTER="srw-aks-dev"
ACR_NAME="srwregistrydev"          # must be globally unique, lowercase, alphanumeric
IDENTITY_NAME="srw-api-identity"
AKS_NODE_VM_SIZE="Standard_D2s_v3"
AKS_MIN_NODES=2
AKS_MAX_NODES=5
K8S_SA_NAMESPACE="srw-platform"
K8S_SA_NAME="srw-api"

APPSETTINGS="src/SRW.Api/appsettings.Development.json"

# ── Step 0: Prerequisites ─────────────────────────────────────────────────────
log_step 0 "Checking prerequisites"

check_tool() {
  if ! command -v "$1" &>/dev/null; then
    log_error "'$1' is not installed or not in PATH. Install it and re-run."
  fi
  log_ok "$1 found"
}

check_tool az
check_tool kubectl
check_tool helm

# Detect Python — try python3, python, py
PYTHON_CMD=""
for candidate in python3 python py; do
  if command -v "$candidate" &>/dev/null; then
    # Check it's actually Python and not the Windows Store stub
    if "$candidate" --version 2>&1 | grep -qi "python"; then
      PYTHON_CMD="$candidate"
      log_ok "$candidate found ($($candidate --version 2>&1))"
      break
    fi
  fi
done

if [[ -z "$PYTHON_CMD" ]]; then
  log_warn "Python not found — appsettings will not be auto-patched at the end"
  log_warn "  Install: winget install Python.Python.3.12"
  log_warn "  After install, restart Git Bash and re-run this script"
fi

# ── Step 1: Azure Login ───────────────────────────────────────────────────────
log_step 1 "Verifying Azure login"

if ! az account show &>/dev/null; then
  log_info "Not logged in — launching az login..."
  az login
fi

SUBSCRIPTION_ID=$(aztsv account show --query id -o tsv)
ACCOUNT_NAME=$(aztsv account show --query name -o tsv)
log_ok "Subscription : ${ACCOUNT_NAME} (${SUBSCRIPTION_ID})"

# Explicitly set the active subscription so all subsequent commands use it
az account set --subscription "$SUBSCRIPTION_ID"

# ── Step 2: Resource Group ────────────────────────────────────────────────────
log_step 2 "Resource Group"

if az group show --name "$RG" &>/dev/null; then
  log_warn "Resource group '$RG' already exists — skipping creation"
else
  az group create --name "$RG" --location "$LOCATION" --output none
  log_ok "Created resource group: $RG ($LOCATION)"
fi

# ── Step 3: Cosmos DB ─────────────────────────────────────────────────────────
log_step 3 "Azure Cosmos DB (NoSQL)"

if az cosmosdb show --name "$COSMOS_ACCOUNT" --resource-group "$RG" &>/dev/null; then
  log_warn "Cosmos DB account '$COSMOS_ACCOUNT' already exists — skipping creation"
else
  log_info "Creating Cosmos DB account (this takes ~2 minutes)..."
  az cosmosdb create \
    --name "$COSMOS_ACCOUNT" \
    --resource-group "$RG" \
    --kind GlobalDocumentDB \
    --capabilities EnableServerless \
    --default-consistency-level Session \
    --locations regionName="$LOCATION" \
    --output none
  log_ok "Created Cosmos DB account: $COSMOS_ACCOUNT"
fi

COSMOS_ENDPOINT=$(aztsv cosmosdb show \
  --name "$COSMOS_ACCOUNT" --resource-group "$RG" \
  --query documentEndpoint -o tsv)

COSMOS_KEY=$(aztsv cosmosdb keys list \
  --name "$COSMOS_ACCOUNT" --resource-group "$RG" \
  --query primaryMasterKey -o tsv)

log_ok "Cosmos endpoint : $COSMOS_ENDPOINT"
log_ok "Cosmos key      : ${COSMOS_KEY:0:8}... (truncated)"

# ── Step 4: Service Bus ───────────────────────────────────────────────────────
log_step 4 "Azure Service Bus + Queues"

if az servicebus namespace show --name "$SB_NAMESPACE" --resource-group "$RG" &>/dev/null; then
  log_warn "Service Bus namespace '$SB_NAMESPACE' already exists — skipping creation"
else
  az servicebus namespace create \
    --name "$SB_NAMESPACE" --resource-group "$RG" \
    --sku Standard --location "$LOCATION" --output none
  log_ok "Created Service Bus namespace: $SB_NAMESPACE"
fi

SB_FQNS="${SB_NAMESPACE}.servicebus.windows.net"

# Fetch the RootManageSharedAccessKey connection string for local-dev fallback.
# (Auto-created when the namespace was created — no admin permission needed.)
SB_CONNECTION_STRING=$(aztsv servicebus namespace authorization-rule keys list \
  --resource-group "$RG" --namespace-name "$SB_NAMESPACE" \
  --name RootManageSharedAccessKey \
  --query primaryConnectionString -o tsv)
log_ok "Service Bus connection string captured (for local dev)"

create_queue() {
  local QUEUE_NAME="$1"
  if az servicebus queue show \
       --name "$QUEUE_NAME" --namespace-name "$SB_NAMESPACE" \
       --resource-group "$RG" &>/dev/null; then
    log_warn "Queue '$QUEUE_NAME' already exists — skipping"
  else
    az servicebus queue create \
      --name "$QUEUE_NAME" --namespace-name "$SB_NAMESPACE" \
      --resource-group "$RG" --max-delivery-count 10 \
      --lock-duration PT5M --default-message-time-to-live P14D \
      --output none
    log_ok "Created queue: $QUEUE_NAME"
  fi
}

create_queue "srw-workspace-provision"
create_queue "srw-session-stop"
create_queue "srw-workspace-cleanup"

# ── Step 5: Container Registry ────────────────────────────────────────────────
log_step 5 "Azure Container Registry"

if az acr show --name "$ACR_NAME" --resource-group "$RG" &>/dev/null; then
  log_warn "ACR '$ACR_NAME' already exists — skipping creation"
else
  az acr create \
    --name "$ACR_NAME" --resource-group "$RG" \
    --sku Basic --location "$LOCATION" --output none
  log_ok "Created ACR: $ACR_NAME"
fi

ACR_LOGIN_SERVER=$(aztsv acr show \
  --name "$ACR_NAME" --resource-group "$RG" \
  --query loginServer -o tsv)
log_ok "ACR login server: $ACR_LOGIN_SERVER"

# ── Step 6: AKS Cluster ───────────────────────────────────────────────────────
log_step 6 "AKS Cluster (this takes ~8-10 minutes)"

if az aks show --name "$AKS_CLUSTER" --resource-group "$RG" &>/dev/null; then
  log_warn "AKS cluster '$AKS_CLUSTER' already exists — skipping creation"
else
  log_info "Creating AKS cluster (node size: $AKS_NODE_VM_SIZE, min: $AKS_MIN_NODES, max: $AKS_MAX_NODES)..."
  az aks create \
    --name "$AKS_CLUSTER" --resource-group "$RG" --location "$LOCATION" \
    --node-count "$AKS_MIN_NODES" --min-count "$AKS_MIN_NODES" --max-count "$AKS_MAX_NODES" \
    --enable-cluster-autoscaler \
    --node-vm-size "$AKS_NODE_VM_SIZE" \
    --enable-oidc-issuer --enable-workload-identity --enable-managed-identity \
    --attach-acr "$ACR_NAME" --network-plugin azure --generate-ssh-keys \
    --output none
  log_ok "AKS cluster created: $AKS_CLUSTER"
fi

log_info "Enabling Azure File CSI driver..."
az aks update \
  --name "$AKS_CLUSTER" --resource-group "$RG" \
  --enable-file-driver --output none
log_ok "Azure File CSI driver enabled"

log_info "Fetching kubeconfig..."
az aks get-credentials \
  --name "$AKS_CLUSTER" --resource-group "$RG" --overwrite-existing
log_ok "kubectl context set to: $AKS_CLUSTER"

AKS_OIDC_ISSUER=$(aztsv aks show \
  --name "$AKS_CLUSTER" --resource-group "$RG" \
  --query oidcIssuerProfile.issuerUrl -o tsv)
log_ok "OIDC issuer: $AKS_OIDC_ISSUER"

# ── Step 7: Managed Identity ──────────────────────────────────────────────────
log_step 7 "User-Assigned Managed Identity"

if az identity show --name "$IDENTITY_NAME" --resource-group "$RG" &>/dev/null; then
  log_warn "Managed identity '$IDENTITY_NAME' already exists — skipping creation"
else
  az identity create \
    --name "$IDENTITY_NAME" --resource-group "$RG" \
    --location "$LOCATION" --output none
  log_ok "Created managed identity: $IDENTITY_NAME"
fi

IDENTITY_CLIENT_ID=$(aztsv identity show \
  --name "$IDENTITY_NAME" --resource-group "$RG" \
  --query clientId -o tsv)

IDENTITY_OBJECT_ID=$(aztsv identity show \
  --name "$IDENTITY_NAME" --resource-group "$RG" \
  --query principalId -o tsv)

log_ok "Identity client ID : $IDENTITY_CLIENT_ID"
log_ok "Identity object ID : $IDENTITY_OBJECT_ID"

# ── Step 8: RBAC — Managed Identity ──────────────────────────────────────────
log_step 8 "RBAC — Managed Identity role assignments"

SCOPE_RG="/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RG}"

SCOPE_SB=$(aztsv servicebus namespace show \
  --name "$SB_NAMESPACE" --resource-group "$RG" --query id -o tsv)

SCOPE_COSMOS=$(aztsv cosmosdb show \
  --name "$COSMOS_ACCOUNT" --resource-group "$RG" --query id -o tsv)

log_ok "Scope RG     : $SCOPE_RG"
log_ok "Scope SB     : $SCOPE_SB"
log_ok "Scope Cosmos : $SCOPE_COSMOS"

# Track skipped role assignments so we can report them at the end
SKIPPED_ROLES=()

assign_role() {
  local ROLE="$1" ASSIGNEE="$2" SCOPE="$3" LABEL="$4" PRINCIPAL_TYPE="$5"
  local output exit_code
  set +e
  output=$(az role assignment create \
    --role "$ROLE" \
    --assignee-object-id "$ASSIGNEE" \
    --assignee-principal-type "$PRINCIPAL_TYPE" \
    --scope "$SCOPE" 2>&1)
  exit_code=$?
  set -e

  if [[ $exit_code -eq 0 ]]; then
    log_ok "Assigned '$ROLE' on $LABEL"
  elif echo "$output" | grep -qiE "already exists|RoleAssignmentExists|already been assigned"; then
    log_warn "Role '$ROLE' on $LABEL already assigned — skipping"
  elif echo "$output" | grep -qiE "MissingSubscription|AuthorizationFailed|does not have authorization"; then
    log_warn "INSUFFICIENT PERMISSIONS to assign '$ROLE' on $LABEL"
    log_warn "  → Ask an admin (Owner / User Access Administrator) to run:"
    log_warn "      az role assignment create --role \"$ROLE\" \\"
    log_warn "        --assignee-object-id $ASSIGNEE --assignee-principal-type $PRINCIPAL_TYPE \\"
    log_warn "        --scope $SCOPE"
    SKIPPED_ROLES+=("$ROLE on $LABEL (assignee=$ASSIGNEE, type=$PRINCIPAL_TYPE)")
  else
    log_warn "Unexpected error assigning '$ROLE' on $LABEL: $output"
    SKIPPED_ROLES+=("$ROLE on $LABEL (assignee=$ASSIGNEE, type=$PRINCIPAL_TYPE)")
  fi
}

assign_role "Storage Account Contributor"  "$IDENTITY_OBJECT_ID" "$SCOPE_RG" "resource group"    "ServicePrincipal"
assign_role "Azure Service Bus Data Owner" "$IDENTITY_OBJECT_ID" "$SCOPE_SB" "Service Bus"       "ServicePrincipal"

log_info "Assigning Cosmos DB Built-in Data Contributor to managed identity..."
set +e
cosmos_output=$(az cosmosdb sql role assignment create \
  --account-name "$COSMOS_ACCOUNT" --resource-group "$RG" \
  --role-definition-name "Cosmos DB Built-in Data Contributor" \
  --principal-id "$IDENTITY_OBJECT_ID" \
  --scope "$SCOPE_COSMOS" 2>&1)
cosmos_exit=$?
set -e
if [[ $cosmos_exit -eq 0 ]]; then
  log_ok "Assigned Cosmos DB Built-in Data Contributor to managed identity"
elif echo "$cosmos_output" | grep -qiE "already exists|conflict"; then
  log_warn "Cosmos DB role already assigned to managed identity — skipping"
elif echo "$cosmos_output" | grep -qiE "MissingSubscription|AuthorizationFailed|does not have authorization"; then
  log_warn "INSUFFICIENT PERMISSIONS for Cosmos DB role on managed identity"
  log_warn "  → Ask an admin to run:"
  log_warn "      az cosmosdb sql role assignment create --account-name $COSMOS_ACCOUNT \\"
  log_warn "        --resource-group $RG --role-definition-name 'Cosmos DB Built-in Data Contributor' \\"
  log_warn "        --principal-id $IDENTITY_OBJECT_ID --scope $SCOPE_COSMOS"
  SKIPPED_ROLES+=("Cosmos DB Built-in Data Contributor on Cosmos (managed identity)")
else
  log_warn "Cosmos DB role assignment failed: $cosmos_output"
  SKIPPED_ROLES+=("Cosmos DB Built-in Data Contributor on Cosmos (managed identity)")
fi

# ── Step 9: RBAC — Local Developer ───────────────────────────────────────────
log_step 9 "RBAC — Local developer role assignments"

MY_OBJECT_ID=$(az ad signed-in-user show --query id -o tsv 2>/dev/null | tr -d '\r' || true)

if [[ -z "$MY_OBJECT_ID" ]]; then
  log_warn "Could not determine signed-in user object ID — skipping local dev roles"
else
  assign_role "Storage Account Contributor"  "$MY_OBJECT_ID" "$SCOPE_RG" "resource group (local dev)" "User"
  assign_role "Azure Service Bus Data Owner" "$MY_OBJECT_ID" "$SCOPE_SB" "Service Bus (local dev)"    "User"

  log_info "Assigning Cosmos DB role to your local identity..."
  set +e
  cosmos_me_output=$(az cosmosdb sql role assignment create \
    --account-name "$COSMOS_ACCOUNT" --resource-group "$RG" \
    --role-definition-name "Cosmos DB Built-in Data Contributor" \
    --principal-id "$MY_OBJECT_ID" \
    --scope "$SCOPE_COSMOS" 2>&1)
  cosmos_me_exit=$?
  set -e
  if [[ $cosmos_me_exit -eq 0 ]]; then
    log_ok "Assigned Cosmos DB Built-in Data Contributor to your identity"
  elif echo "$cosmos_me_output" | grep -qiE "already exists|conflict"; then
    log_warn "Cosmos DB role already assigned to your identity — skipping"
  elif echo "$cosmos_me_output" | grep -qiE "MissingSubscription|AuthorizationFailed|does not have authorization"; then
    log_warn "INSUFFICIENT PERMISSIONS for Cosmos DB role on your identity"
    SKIPPED_ROLES+=("Cosmos DB Built-in Data Contributor on Cosmos (your account)")
  else
    log_warn "Cosmos DB role assignment for your identity failed: $cosmos_me_output"
    SKIPPED_ROLES+=("Cosmos DB Built-in Data Contributor on Cosmos (your account)")
  fi
fi

# ── Step 10: Federated Identity Credential ────────────────────────────────────
log_step 10 "Federated Identity Credential (Workload Identity)"

if az identity federated-credential show \
     --name "srw-api-federated" --identity-name "$IDENTITY_NAME" \
     --resource-group "$RG" &>/dev/null; then
  log_warn "Federated credential already exists — skipping"
else
  az identity federated-credential create \
    --name "srw-api-federated" \
    --identity-name "$IDENTITY_NAME" \
    --resource-group "$RG" \
    --issuer "$AKS_OIDC_ISSUER" \
    --subject "system:serviceaccount:${K8S_SA_NAMESPACE}:${K8S_SA_NAME}" \
    --audiences "api://AzureADTokenExchange" \
    --output none
  log_ok "Federated credential created"
fi

# ── Step 11: Kubernetes Manifests ─────────────────────────────────────────────
log_step 11 "Kubernetes namespace + RBAC"

kubectl create namespace "$K8S_SA_NAMESPACE" \
  --dry-run=client -o yaml | kubectl apply -f - > /dev/null
log_ok "Namespace '$K8S_SA_NAMESPACE' ensured"

kubectl apply -f k8s/manifests/00-cluster-setup.yaml
log_ok "Applied 00-cluster-setup.yaml"

# ── Step 12: ingress-nginx ────────────────────────────────────────────────────
log_step 12 "ingress-nginx (Helm)"

helm repo add ingress-nginx https://kubernetes.github.io/ingress-nginx --force-update > /dev/null
helm repo update > /dev/null

helm upgrade --install ingress-nginx ingress-nginx/ingress-nginx \
  --namespace ingress-nginx --create-namespace \
  --set controller.replicaCount=2 \
  --wait --timeout 5m

log_info "Waiting for ingress LoadBalancer IP (up to 2 minutes)..."
INGRESS_IP=""
for i in $(seq 1 24); do
  INGRESS_IP=$(kubectl get service ingress-nginx-controller \
    -n ingress-nginx \
    -o jsonpath='{.status.loadBalancer.ingress[0].ip}' 2>/dev/null | tr -d '\r' || true)
  [[ -n "$INGRESS_IP" ]] && break
  sleep 5
done

if [[ -z "$INGRESS_IP" ]]; then
  log_warn "LoadBalancer IP not assigned yet."
  log_warn "  Run later: kubectl get svc ingress-nginx-controller -n ingress-nginx"
  INGRESS_IP="PENDING"
else
  log_ok "Ingress public IP: $INGRESS_IP"
fi

# ── Step 13: Kubernetes Secret ────────────────────────────────────────────────
log_step 13 "Kubernetes secret: srw-api-secrets"

kubectl create secret generic srw-api-secrets \
  --namespace "$K8S_SA_NAMESPACE" \
  --from-literal=cosmos-endpoint="$COSMOS_ENDPOINT" \
  --from-literal=cosmos-key="$COSMOS_KEY" \
  --from-literal=servicebus-namespace="$SB_FQNS" \
  --dry-run=client -o yaml | kubectl apply -f - > /dev/null
log_ok "Secret 'srw-api-secrets' applied in namespace $K8S_SA_NAMESPACE"

# ── Step 14: Patch appsettings.Development.json ───────────────────────────────
log_step 14 "Patching $APPSETTINGS"

# Always write a values file the user can reference even if Python isn't available
VALUES_FILE="scripts/.azure-values.txt"
cat > "$VALUES_FILE" <<EOF
# Azure resources created by setup-azure.sh
# Paste these into src/SRW.Api/appsettings.Development.json
# ─────────────────────────────────────────────────────────────────────────────

Cosmos:Endpoint                       = $COSMOS_ENDPOINT
Cosmos:AccountKey                     = $COSMOS_KEY

ServiceBus:FullyQualifiedNamespace    = $SB_FQNS
ServiceBus:ConnectionString           = $SB_CONNECTION_STRING

Azure:Region                          = $LOCATION
Azure:AksClusterName                  = $AKS_CLUSTER
Azure:AksResourceGroup                = $RG
Azure:IngressDomain                   = $INGRESS_IP

# Other useful values:
Identity Client ID                    = $IDENTITY_CLIENT_ID
Identity Object ID                    = $IDENTITY_OBJECT_ID
ACR Login Server                      = $ACR_LOGIN_SERVER
EOF
log_ok "Values written to $VALUES_FILE"

if [[ ! -f "$APPSETTINGS" ]]; then
  log_warn "$APPSETTINGS not found — run from the repo root"
elif [[ -z "$PYTHON_CMD" ]]; then
  log_warn "Python not available — paste values from $VALUES_FILE manually into $APPSETTINGS"
else
  "$PYTHON_CMD" - <<PYEOF
import json

path = "$APPSETTINGS"
with open(path, "r") as f:
    cfg = json.load(f)

cfg["Cosmos"]["Endpoint"]                    = "$COSMOS_ENDPOINT"
cfg["Cosmos"]["AccountKey"]                  = "$COSMOS_KEY"
cfg["Azure"]["SubscriptionId"]               = "$SUBSCRIPTION_ID"
cfg["Azure"]["AksClusterName"]               = "$AKS_CLUSTER"
cfg["Azure"]["AksResourceGroup"]             = "$RG"
cfg["Azure"]["Region"]                       = "$LOCATION"
cfg["Azure"]["IngressDomain"]                = "$INGRESS_IP"
cfg["ServiceBus"]["FullyQualifiedNamespace"] = "$SB_FQNS"
cfg["ServiceBus"]["ConnectionString"]        = "$SB_CONNECTION_STRING"

with open(path, "w") as f:
    json.dump(cfg, f, indent=2)
    f.write("\n")
PYEOF
  log_ok "$APPSETTINGS updated with real Azure values"
fi

# ── Step 15: Patch 10-api-deployment.yaml ─────────────────────────────────────
log_step 15 "Patching k8s/manifests/10-api-deployment.yaml"

MANIFEST="k8s/manifests/10-api-deployment.yaml"
if [[ -f "$MANIFEST" ]]; then
  sed -i "s|<ACR_NAME>|${ACR_NAME}|g"                    "$MANIFEST"
  sed -i "s|<IDENTITY_CLIENT_ID>|${IDENTITY_CLIENT_ID}|g" "$MANIFEST"
  sed -i "s|research\.yourdomain\.com|${INGRESS_IP}|g"    "$MANIFEST"
  log_ok "$MANIFEST patched"
else
  log_warn "$MANIFEST not found — skipping"
fi

# ── Summary ───────────────────────────────────────────────────────────────────
hr

if [[ ${#SKIPPED_ROLES[@]} -gt 0 ]]; then
  echo -e "\n${YELLOW}${BOLD}⚠  ${#SKIPPED_ROLES[@]} role assignment(s) could not be created${NC}"
  echo -e "${YELLOW}Your account does not have 'User Access Administrator' or 'Owner' role.${NC}"
  echo -e "${YELLOW}Send the following list to your Azure subscription admin to grant manually:${NC}\n"
  for role in "${SKIPPED_ROLES[@]}"; do
    echo -e "  ${YELLOW}•${NC} $role"
  done
  echo ""
  echo -e "${BOLD}Required roles for admin to assign:${NC}"
  echo -e "  Subscription scope: $SCOPE_RG"
  echo -e "  - 'Storage Account Contributor' for object ID $IDENTITY_OBJECT_ID (ServicePrincipal)"
  echo -e "  - 'Storage Account Contributor' for object ID ${MY_OBJECT_ID:-<your-object-id>} (User)"
  echo ""
  echo -e "  Service Bus scope: $SCOPE_SB"
  echo -e "  - 'Azure Service Bus Data Owner' for both above"
  echo ""
  echo -e "  Cosmos DB scope: $SCOPE_COSMOS"
  echo -e "  - 'Cosmos DB Built-in Data Contributor' for both above (use az cosmosdb sql role assignment create)"
  hr
fi

echo -e "\n${GREEN}${BOLD}✓ Azure infrastructure provisioning complete${NC}\n"
printf "  %-32s %s\n" "Resource Group:"     "$RG"
printf "  %-32s %s\n" "Cosmos DB:"          "$COSMOS_ACCOUNT"
printf "  %-32s %s\n" "Cosmos Endpoint:"    "$COSMOS_ENDPOINT"
printf "  %-32s %s\n" "Service Bus:"        "$SB_FQNS"
printf "  %-32s %s\n" "Container Registry:" "$ACR_LOGIN_SERVER"
printf "  %-32s %s\n" "AKS Cluster:"        "$AKS_CLUSTER"
printf "  %-32s %s\n" "Managed Identity:"   "$IDENTITY_NAME"
printf "  %-32s %s\n" "Identity Client ID:" "$IDENTITY_CLIENT_ID"
printf "  %-32s %s\n" "Ingress IP:"         "$INGRESS_IP"

echo -e "\n${BOLD}Next steps:${NC}"
echo -e "  1. Build and push API image:"
echo -e "       az acr login --name $ACR_NAME"
echo -e "       docker build -t $ACR_LOGIN_SERVER/srw-api:1.0.0 -f src/SRW.Api/Dockerfile ."
echo -e "       docker push $ACR_LOGIN_SERVER/srw-api:1.0.0"
echo ""
echo -e "  2. Deploy to AKS:"
echo -e "       kubectl apply -f k8s/manifests/10-api-deployment.yaml"
echo -e "       kubectl rollout status deployment/srw-api -n srw-platform"
echo ""
echo -e "  3. Run locally against real Azure:"
echo -e "       dotnet run --project src/SRW.Api"
hr
