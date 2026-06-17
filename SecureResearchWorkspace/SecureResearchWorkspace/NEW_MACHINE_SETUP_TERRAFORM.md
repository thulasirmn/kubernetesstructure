# New Machine Setup — Terraform & Service Bus Configuration

Companion to [`NEW_MACHINE_SETUP.md`](NEW_MACHINE_SETUP.md).  
Covers the exact `appsettings.Development.json` values needed for Terraform-backed session
launch and the Azure Service Bus queues.

---

## 1. Service Bus

Add this block to `src/SRW.Api/appsettings.Development.json`:

```json
"ServiceBus": {
  "FullyQualifiedNamespace": "srw-servicebus-dev.servicebus.windows.net",
  "ConnectionString": "Endpoint=sb://srw-servicebus-dev.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=<GET-FROM-AZURE-PORTAL-OR-TEAM>",
  "WorkspaceProvisionQueue": "srw-workspace-provision",
  "SessionStopQueue": "srw-session-stop",
  "WorkspaceCleanupQueue": "srw-workspace-cleanup"
}
```

> **Secret:** The `SharedAccessKey` value must be retrieved from the Azure Portal
> (`srw-servicebus-dev` namespace → Shared access policies → RootManageSharedAccessKey → Primary key)
> or from your team's secret store. **Never commit the real key to the repo.**
>
> Alternatively, omit `ConnectionString` entirely and use `az login` with the
> **Azure Service Bus Data Owner** role on the namespace — `DefaultAzureCredential`
> will pick it up automatically.

---

## 2. Terraform

Add this block to `src/SRW.Api/appsettings.Development.json`:

```json
"Terraform": {
  "TerraformBinaryPath": "terraform",
  "ModulesBasePath": "terraform/modules",
  "WorkingRootDir": "C:/temp/srw-terraform",
  "PluginCacheDir": "C:/temp/terraform-plugins",
  "StateStorageAccount": "srwterraformstate",
  "StateResourceGroup": "srw-dev-rg",
  "StateContainer": "srw-tf-state",
  "SubscriptionId": "<AZURE-SUBSCRIPTION-ID>",
  "TenantId": "<AZURE-TENANT-ID>",
  "ClientId": "",
  "Region": "eastus",
  "IngressDomain": "research.example.com"
}
```

| Key | Notes |
|---|---|
| `TerraformBinaryPath` | Path to `terraform.exe`. If Terraform is on your `PATH`, `"terraform"` works. Otherwise use a full path like `C:/tools/terraform/terraform.exe`. |
| `WorkingRootDir` | Terraform working directories are created here at runtime. Use a local temp path. On Linux/Mac use `/tmp/srw-terraform`. |
| `PluginCacheDir` | Terraform provider plugin cache. Avoids re-downloading the azurerm/kubernetes providers on every init. |
| `StateStorageAccount` | Azure Storage Account that holds Terraform remote state. Must exist before the first `terraform apply`. |
| `StateResourceGroup` | Resource group of the state storage account. |
| `StateContainer` | Blob container name inside the state storage account (e.g. `srw-tf-state`). |
| `SubscriptionId` | Azure subscription where workspaces and AKS live. |
| `TenantId` | Azure AD tenant. Required when `ClientId` is set (service principal auth). Leave blank to use `az login` / Managed Identity. |
| `ClientId` | Leave blank for local dev — `DefaultAzureCredential` uses `az login`. |

---

## 3. Azure RBAC for Terraform state

The identity running the API (your `az login` account locally) needs access to the Terraform
state storage account:

```
Storage Blob Data Contributor  on  srwterraformstate (storage account)
```

Without this, `terraform init` will fail with a 403 when trying to read/write the `.tfstate`
blob.

---

## 4. Verify Terraform is reachable

```powershell
terraform version
```

Then do a quick sanity-check that the state backend is accessible:

```powershell
cd terraform/modules/workspace-storage
terraform init -backend-config="storage_account_name=srwterraformstate" `
               -backend-config="container_name=srw-tf-state" `
               -backend-config="resource_group_name=srw-dev-rg" `
               -backend-config="key=test.tfstate"
```

A successful `init` confirms credentials, network access, and blob container permissions
are all working.

---

## 5. Complete `appsettings.Development.json` template

```json
{
  "Cosmos": {
    "Endpoint": "https://srw-cosmos.documents.azure.com:443/",
    "AccountKey": "<COSMOS-KEY-OR-LEAVE-BLANK-FOR-MANAGED-IDENTITY>"
  },
  "Azure": {
    "SubscriptionId": "<AZURE-SUBSCRIPTION-ID>",
    "Region": "eastus",
    "AksClusterName": "srw-aks-dev",
    "AksResourceGroup": "srw-dev-rg",
    "IngressDomain": "research.example.com",
    "AllowedSubnetIds": []
  },
  "ServiceBus": {
    "FullyQualifiedNamespace": "srw-servicebus-dev.servicebus.windows.net",
    "ConnectionString": "Endpoint=sb://srw-servicebus-dev.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=<GET-FROM-AZURE-PORTAL>",
    "WorkspaceProvisionQueue": "srw-workspace-provision",
    "SessionStopQueue": "srw-session-stop",
    "WorkspaceCleanupQueue": "srw-workspace-cleanup"
  },
  "Terraform": {
    "TerraformBinaryPath": "terraform",
    "ModulesBasePath": "terraform/modules",
    "WorkingRootDir": "C:/temp/srw-terraform",
    "PluginCacheDir": "C:/temp/terraform-plugins",
    "StateStorageAccount": "srwterraformstate",
    "StateResourceGroup": "srw-dev-rg",
    "StateContainer": "srw-tf-state",
    "SubscriptionId": "<AZURE-SUBSCRIPTION-ID>",
    "TenantId": "<AZURE-TENANT-ID>",
    "ClientId": "",
    "Region": "eastus",
    "IngressDomain": "research.example.com"
  },
  "BackgroundJobs": {
    "SessionStatusPollSeconds": 15,
    "IdleReaperIntervalMinutes": 10,
    "IdleSessionThresholdHours": 8
  }
}
```

> `appsettings.Development.json` is in `.gitignore` — never commit this file.
