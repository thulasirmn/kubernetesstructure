variable "workspace_id" {
  type        = string
  description = "SRW workspace GUID"
}

variable "k8s_namespace" {
  type        = string
  description = "Kubernetes namespace name for this workspace (e.g. ws-mylab-a1b2c3d4)"
}

variable "storage_account_name" {
  type        = string
  description = "Azure storage account name — written into the CSI driver Secret"
}

variable "storage_account_key" {
  type        = string
  sensitive   = true
  description = "Azure storage account primary key — written into the CSI driver Secret"
}
