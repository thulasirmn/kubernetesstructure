terraform {
  required_providers {
    kubernetes = {
      source  = "hashicorp/kubernetes"
      version = "~> 2.0"
    }
  }
  # Backend configured at init time via -backend-config flags:
  #   -backend-config="key=workspaces/<k8sNamespace>-k8s.tfstate"
  backend "azurerm" {}
}

provider "kubernetes" {
  # Auto-detects in-cluster config from env vars (KUBERNETES_SERVICE_HOST / PORT)
  # when running inside an AKS pod. Falls back to ~/.kube/config for local dev.
}

resource "kubernetes_namespace" "workspace" {
  metadata {
    name = var.k8s_namespace
    labels = {
      "srw.io/workspace-id" = var.workspace_id
      "srw.io/managed-by"   = "srw-terraform"
    }
  }
}

# Stores the Azure storage account credentials for the CSI driver (file.csi.azure.com).
# The key is passed as TF_VAR_storage_account_key (env var) — never written to tfvars.
resource "kubernetes_secret" "storage_creds" {
  metadata {
    name      = "azure-storage-creds"
    namespace = kubernetes_namespace.workspace.metadata[0].name
  }

  data = {
    azurestorageaccountname = var.storage_account_name
    azurestorageaccountkey  = var.storage_account_key
  }
}

# Default-deny NetworkPolicy: only pods in the ingress-nginx namespace can reach
# pods in this workspace namespace — workspaces are isolated from each other.
resource "kubernetes_network_policy" "default_deny" {
  metadata {
    name      = "default-deny"
    namespace = kubernetes_namespace.workspace.metadata[0].name
  }

  spec {
    pod_selector {}

    ingress {
      from {
        namespace_selector {
          match_labels = {
            name = "ingress-nginx"
          }
        }
      }
    }

    policy_types = ["Ingress"]
  }
}
