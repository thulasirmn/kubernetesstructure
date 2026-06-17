variable "session_id" {
  type        = string
  description = "SRW session GUID"
}

variable "workspace_id" {
  type        = string
  description = "SRW workspace GUID"
}

variable "user_id" {
  type        = string
  description = "User identifier (from identity provider)"
}

variable "app_type" {
  type        = string
  description = "Application type: jupyter | rstudio | custom"
}

variable "k8s_namespace" {
  type        = string
  description = "Kubernetes namespace for this workspace"
}

variable "ingress_path" {
  type        = string
  description = "Ingress path prefix for this session (e.g. /s/abc123de)"
}

variable "ingress_domain" {
  type        = string
  description = "Public domain or IP for the ingress controller"
}

variable "container_image" {
  type        = string
  description = "Container image for the application"
}

variable "container_port" {
  type        = number
  description = "Port the application listens on inside the container"
}

variable "cpu_request" {
  type    = string
  default = "500m"
}

variable "cpu_limit" {
  type    = string
  default = "2"
}

variable "memory_request" {
  type    = string
  default = "1Gi"
}

variable "memory_limit" {
  type    = string
  default = "4Gi"
}

variable "mount_path" {
  type    = string
  default = "/workspace"
  description = "Path inside the container where the workspace file share is mounted"
}

variable "file_share_name" {
  type        = string
  description = "Azure File Share name — referenced by the CSI volume"
}

variable "environment_json" {
  type    = string
  default = "{}"
  description = "JSON object of environment variable key/value pairs"
}

variable "command_json" {
  type    = string
  default = "[]"
  description = "JSON array of command strings. Use __BASE_URL__ token for the ingress path."
}
