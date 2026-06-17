variable "resource_group" {
  type        = string
  description = "Azure resource group for the storage account"
}

variable "storage_account_name" {
  type        = string
  description = "Storage account name (srw<workspaceId>, globally unique)"
}

variable "file_share_name" {
  type        = string
  default     = "workspace-share"
  description = "SMB file share name within the storage account"
}

variable "quota_gib" {
  type        = number
  default     = 100
  description = "File share quota in GiB"
}

variable "region" {
  type        = string
  default     = "eastus"
  description = "Azure region"
}

variable "workspace_id" {
  type        = string
  description = "SRW workspace GUID — applied as a resource tag"
}
