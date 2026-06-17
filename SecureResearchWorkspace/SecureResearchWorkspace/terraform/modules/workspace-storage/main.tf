terraform {
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.0"
    }
  }
  # Backend configured at init time via -backend-config flags:
  #   -backend-config="storage_account_name=<state-account>"
  #   -backend-config="resource_group_name=<state-rg>"
  #   -backend-config="container_name=srw-tf-state"
  #   -backend-config="key=workspaces/<storageAccountName>-storage.tfstate"
  backend "azurerm" {}
}

provider "azurerm" {
  features {}
  # Credentials supplied via ARM_* environment variables:
  #   In AKS (Workload Identity): ARM_USE_OIDC, ARM_CLIENT_ID, ARM_TENANT_ID, ARM_OIDC_TOKEN_FILE_PATH
  #   Locally: ARM_SUBSCRIPTION_ID set; az login credentials used automatically
}

resource "azurerm_storage_account" "workspace" {
  name                     = var.storage_account_name
  resource_group_name      = var.resource_group
  location                 = var.region
  account_tier             = "Standard"
  account_replication_type = "LRS"
  account_kind             = "StorageV2"
  access_tier              = "Hot"

  min_tls_version               = "TLS1_2"
  https_traffic_only_enabled    = true
  shared_access_key_enabled     = true

  tags = {
    "srw-workspace-id" = var.workspace_id
    "srw-managed-by"   = "srw-terraform"
  }
}

resource "azurerm_storage_share" "workspace" {
  name                 = var.file_share_name
  storage_account_name = azurerm_storage_account.workspace.name
  quota                = var.quota_gib
  access_tier          = "TransactionOptimized"
  enabled_protocol     = "SMB"
}
