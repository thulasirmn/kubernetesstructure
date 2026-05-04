# Azure Resource Setup Guide

Complete step-by-step Azure CLI commands to provision every resource the SRW platform needs.
Run these once before starting the API for the first time.

---

## Prerequisites

```bash
# Install Azure CLI (if not already installed)
# https://learn.microsoft.com/en-us/cli/azure/install-azure-cli

# Install kubectl
az aks install-cli

# Install Helm
# https://helm.sh/docs/intro/install/

# Log in
az login

# Confirm your active subscription
az account show --query "{name:name, id:id}" -o table

# If you have multiple subscriptions, set the one you want to use
az account set --subscription "<YOUR_SUBSCRIPTION_ID>"
```

---

## Step 0 — Set Variables

Define these once. Every command below uses them.

```bash
SUBSCRIPTION_ID=$(az account show --query id -o tsv)
LOCATION="eastus"
RG="srw-dev-rg"
COSMOS_ACCOUNT="srw-cosmos-dev"
SB_NAMESPACE="srw-servicebus-dev"
AKS_CLUSTER="srw-aks-dev"
ACR_NAME="srwregistrydev"           # must be globally unique, lowercase, alphanumeric only
IDENTITY_NAME="srw-api-identity"
```

---

## Step 1 — Resource Group

```bash
az group create \
  --name $RG \
  --location $LOCATION
```

---

## Step 2 — Azure Cosmos DB (NoSQL)

```bash
# Create account (serverless = pay per request, cheapest for dev)
az cosmosdb create \
  --name $COSMOS_ACCOUNT \
  --resource-group $RG \
  --kind GlobalDocumentDB \
  --capabilities EnableServerless \
  --default-consistency-level Session \
  --locations regionName=$LOCATION

# Get the endpoint URI (copy this into appsettings)
az cosmosdb show \
  --name $COSMOS_ACCOUNT \
  --resource-group $RG \
  --query documentEndpoint -o tsv

# Get the primary key (copy this into appsettings for local dev)
az cosmosdb keys list \
  --name $COSMOS_ACCOUNT \
  --resource-group $RG \
  --query primaryMasterKey -o tsv
```

> The SRW API creates the database (`srw`) and all containers automatically on first startup.
> No manual container creation is needed.

---

## Step 3 — Azure Service Bus

```bash
# Create namespace (Standard tier minimum — required for queues)
az servicebus namespace create \
  --name $SB_NAMESPACE \
  --resource-group $RG \
  --sku Standard \
  --location $LOCATION

# Create the 3 queues
az servicebus queue create \
  --name srw-workspace-provision \
  --namespace-name $SB_NAMESPACE \
  --resource-group $RG \
  --max-delivery-count 10 \
  --lock-duration PT5M \
  --default-message-time-to-live P14D

az servicebus queue create \
  --name srw-session-stop \
  --namespace-name $SB_NAMESPACE \
  --resource-group $RG \
  --max-delivery-count 10 \
  --lock-duration PT5M \
  --default-message-time-to-live P14D

az servicebus queue create \
  --name srw-workspace-cleanup \
  --namespace-name $SB_NAMESPACE \
  --resource-group $RG \
  --max-delivery-count 10 \
  --lock-duration PT15M \
  --default-message-time-to-live P14D

# Get the fully qualified namespace (copy this into appsettings)
az servicebus namespace show \
  --name $SB_NAMESPACE \
  --resource-group $RG \
  --query serviceBusEndpoint -o tsv
# Output: https://srw-servicebus-dev.servicebus.windows.net/
# Use only the hostname part: srw-servicebus-dev.servicebus.windows.net
```

---

## Step 4 — Azure Container Registry (ACR)

Used to store and pull the SRW API Docker image.

```bash
az acr create \
  --name $ACR_NAME \
  --resource-group $RG \
  --sku Basic \
  --location $LOCATION

# Get the login server (used in the deployment manifest)
az acr show \
  --name $ACR_NAME \
  --resource-group $RG \
  --query loginServer -o tsv
# Output: srwregistrydev.azurecr.io
```

---

## Step 5 — AKS Cluster

```bash
# Create cluster with:
#   - Managed Identity (system-assigned)
#   - OIDC issuer + Workload Identity (for pod MSI)
#   - Azure File CSI driver (for workspace file share mounts)
#   - ACR integration (pull images without registry credentials)
#   - Autoscaler enabled (min 2, max 10 nodes)

az aks create \
  --name $AKS_CLUSTER \
  --resource-group $RG \
  --location $LOCATION \
  --node-count 2 \
  --min-count 2 \
  --max-count 10 \
  --enable-cluster-autoscaler \
  --node-vm-size Standard_D4s_v3 \
  --enable-oidc-issuer \
  --enable-workload-identity \
  --enable-managed-identity \
  --attach-acr $ACR_NAME \
  --network-plugin azure \
  --generate-ssh-keys

# Enable the Azure File CSI driver (required for SMB file share mounts)
az aks update \
  --name $AKS_CLUSTER \
  --resource-group $RG \
  --enable-file-driver

# Download credentials so kubectl works locally
az aks get-credentials \
  --name $AKS_CLUSTER \
  --resource-group $RG \
  --overwrite-existing

# Verify connection
kubectl get nodes
```

---

## Step 6 — Managed Identity for the API Pod

```bash
# Create a user-assigned managed identity
az identity create \
  --name $IDENTITY_NAME \
  --resource-group $RG \
  --location $LOCATION

# Save the identity's client ID and object ID
IDENTITY_CLIENT_ID=$(az identity show \
  --name $IDENTITY_NAME \
  --resource-group $RG \
  --query clientId -o tsv)

IDENTITY_OBJECT_ID=$(az identity show \
  --name $IDENTITY_NAME \
  --resource-group $RG \
  --query principalId -o tsv)

echo "Client ID : $IDENTITY_CLIENT_ID"
echo "Object ID : $IDENTITY_OBJECT_ID"
# Copy Client ID into the deployment manifest (annotations) and appsettings
```

---

## Step 7 — RBAC Role Assignments

Grant the managed identity permission to everything the API needs.

```bash
SCOPE_RG="/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RG"
SCOPE_COSMOS=$(az cosmosdb show --name $COSMOS_ACCOUNT --resource-group $RG --query id -o tsv)
SCOPE_SB=$(az servicebus namespace show --name $SB_NAMESPACE --resource-group $RG --query id -o tsv)

# Storage Account Contributor — creates/deletes workspace storage accounts
az role assignment create \
  --role "Storage Account Contributor" \
  --assignee-object-id $IDENTITY_OBJECT_ID \
  --assignee-principal-type ServicePrincipal \
  --scope $SCOPE_RG

# Cosmos DB Built-in Data Contributor — read/write all containers
az cosmosdb sql role assignment create \
  --account-name $COSMOS_ACCOUNT \
  --resource-group $RG \
  --role-definition-name "Cosmos DB Built-in Data Contributor" \
  --principal-id $IDENTITY_OBJECT_ID \
  --scope $SCOPE_COSMOS

# Service Bus Data Owner — send and receive on all queues
az role assignment create \
  --role "Azure Service Bus Data Owner" \
  --assignee-object-id $IDENTITY_OBJECT_ID \
  --assignee-principal-type ServicePrincipal \
  --scope $SCOPE_SB
```

### For local development — grant YOUR OWN identity the same roles

```bash
MY_OBJECT_ID=$(az ad signed-in-user show --query id -o tsv)

az role assignment create \
  --role "Storage Account Contributor" \
  --assignee-object-id $MY_OBJECT_ID \
  --scope $SCOPE_RG

az cosmosdb sql role assignment create \
  --account-name $COSMOS_ACCOUNT \
  --resource-group $RG \
  --role-definition-name "Cosmos DB Built-in Data Contributor" \
  --principal-id $MY_OBJECT_ID \
  --scope $SCOPE_COSMOS

az role assignment create \
  --role "Azure Service Bus Data Owner" \
  --assignee-object-id $MY_OBJECT_ID \
  --scope $SCOPE_SB
```

---

## Step 8 — Federated Identity Credential (Workload Identity)

Links the Kubernetes ServiceAccount `srw-api` (in namespace `srw-platform`) to the managed identity so the pod can call Azure APIs without any keys.

```bash
AKS_OIDC_ISSUER=$(az aks show \
  --name $AKS_CLUSTER \
  --resource-group $RG \
  --query oidcIssuerProfile.issuerUrl -o tsv)

az identity federated-credential create \
  --name srw-api-federated \
  --identity-name $IDENTITY_NAME \
  --resource-group $RG \
  --issuer $AKS_OIDC_ISSUER \
  --subject system:serviceaccount:srw-platform:srw-api \
  --audiences api://AzureADTokenExchange
```

---

## Step 9 — Apply Kubernetes Cluster Manifests

```bash
# Create the srw-platform namespace first
kubectl create namespace srw-platform --dry-run=client -o yaml | kubectl apply -f -

# Apply cluster RBAC (ServiceAccount, ClusterRole, ClusterRoleBinding, ingress-nginx namespace)
kubectl apply -f k8s/manifests/00-cluster-setup.yaml
```

---

## Step 10 — Install ingress-nginx

```bash
helm repo add ingress-nginx https://kubernetes.github.io/ingress-nginx
helm repo update

helm upgrade --install ingress-nginx ingress-nginx/ingress-nginx \
  --namespace ingress-nginx \
  --create-namespace \
  --set controller.service.annotations."service\.beta\.kubernetes\.io/azure-load-balancer-internal"=false \
  --set controller.replicaCount=2 \
  --wait

# Get the public IP (use this for DNS configuration)
kubectl get service ingress-nginx-controller -n ingress-nginx \
  --output jsonpath='{.status.loadBalancer.ingress[0].ip}'
```

Configure your DNS: point `research.yourdomain.com` → the IP printed above (or use it directly in `Azure:IngressDomain` for testing).

---

## Step 11 — Create the API Kubernetes Secret

The API pod reads Cosmos endpoint/key and Service Bus namespace from a K8s Secret.

```bash
COSMOS_ENDPOINT=$(az cosmosdb show --name $COSMOS_ACCOUNT --resource-group $RG --query documentEndpoint -o tsv)
COSMOS_KEY=$(az cosmosdb keys list --name $COSMOS_ACCOUNT --resource-group $RG --query primaryMasterKey -o tsv)

kubectl create secret generic srw-api-secrets \
  --namespace srw-platform \
  --from-literal=cosmos-endpoint="$COSMOS_ENDPOINT" \
  --from-literal=cosmos-key="$COSMOS_KEY" \
  --from-literal=servicebus-namespace="$SB_NAMESPACE.servicebus.windows.net" \
  --dry-run=client -o yaml | kubectl apply -f -
```

---

## Step 12 — Build and Push the API Image

```bash
# Log in to ACR
az acr login --name $ACR_NAME

# Build and push from the solution root
cd SecureResearchWorkspace/SecureResearchWorkspace

docker build -t $ACR_NAME.azurecr.io/srw-api:1.0.0 -f src/SRW.Api/Dockerfile .
docker push $ACR_NAME.azurecr.io/srw-api:1.0.0
```

---

## Step 13 — Deploy the API to AKS

Update `k8s/manifests/10-api-deployment.yaml` with your ACR and identity values, then:

```bash
kubectl apply -f k8s/manifests/10-api-deployment.yaml

# Watch rollout
kubectl rollout status deployment/srw-api -n srw-platform

# Check logs
kubectl logs -n srw-platform -l app=srw-api --tail=50
```

---

## Quick Reference: All Output Values

After running the above, collect these values:

| Value | Command to retrieve |
|---|---|
| Cosmos endpoint | `az cosmosdb show --name srw-cosmos-dev --resource-group srw-dev-rg --query documentEndpoint -o tsv` |
| Cosmos primary key | `az cosmosdb keys list --name srw-cosmos-dev --resource-group srw-dev-rg --query primaryMasterKey -o tsv` |
| Service Bus FQDN | `srw-servicebus-dev.servicebus.windows.net` |
| ACR login server | `az acr show --name srwregistrydev --resource-group srw-dev-rg --query loginServer -o tsv` |
| AKS OIDC issuer | `az aks show --name srw-aks-dev --resource-group srw-dev-rg --query oidcIssuerProfile.issuerUrl -o tsv` |
| Managed Identity client ID | `az identity show --name srw-api-identity --resource-group srw-dev-rg --query clientId -o tsv` |
| Ingress public IP | `kubectl get svc ingress-nginx-controller -n ingress-nginx -o jsonpath='{.status.loadBalancer.ingress[0].ip}'` |
