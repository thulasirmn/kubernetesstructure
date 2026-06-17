terraform {
  required_providers {
    kubernetes = {
      source  = "hashicorp/kubernetes"
      version = "~> 2.0"
    }
  }
  # Backend configured at init time via -backend-config flags:
  #   -backend-config="key=sessions/<sessionId>.tfstate"
  backend "azurerm" {}
}

provider "kubernetes" {
  # Auto-detects in-cluster config or falls back to ~/.kube/config locally.
}

resource "kubernetes_deployment" "session" {
  metadata {
    name      = local.deployment_name
    namespace = var.k8s_namespace
    labels    = local.labels
  }

  spec {
    replicas = 1

    selector {
      match_labels = { app = local.deployment_name }
    }

    template {
      metadata {
        labels = local.labels
      }

      spec {
        automount_service_account_token = false

        container {
          name              = "app"
          image             = var.container_image
          image_pull_policy = "IfNotPresent"

          command = length(local.base_command) > 0 ? local.base_command : null

          port {
            name           = "http"
            container_port = var.container_port
          }

          dynamic "env" {
            for_each = local.base_env
            content {
              name  = env.key
              value = env.value
            }
          }

          resources {
            requests = {
              cpu    = var.cpu_request
              memory = var.memory_request
            }
            limits = {
              cpu    = var.cpu_limit
              memory = var.memory_limit
            }
          }

          volume_mount {
            name       = "workspace-share"
            mount_path = var.mount_path
            sub_path   = local.sanitized_user
          }

          security_context {
            allow_privilege_escalation = false
            read_only_root_filesystem  = false
            run_as_non_root            = false
          }
        }

        volume {
          name = "workspace-share"

          csi {
            driver    = "file.csi.azure.com"
            read_only = false

            volume_attributes = {
              secretName   = "azure-storage-creds"
              shareName    = var.file_share_name
              mountOptions = "dir_mode=0777,file_mode=0777,uid=1000,gid=1000,mfsymlinks,cache=strict,nosharesock"
            }
          }
        }
      }
    }
  }
}

resource "kubernetes_service" "session" {
  metadata {
    name      = local.service_name
    namespace = var.k8s_namespace
    labels    = local.labels
  }

  spec {
    type     = "ClusterIP"
    selector = { app = local.deployment_name }

    port {
      port        = 80
      target_port = var.container_port
      protocol    = "TCP"
    }
  }
}

resource "kubernetes_ingress_v1" "session" {
  metadata {
    name      = local.deployment_name
    namespace = var.k8s_namespace
    labels    = local.labels

    annotations = merge(
      {
        "nginx.ingress.kubernetes.io/proxy-read-timeout" = "3600"
        "nginx.ingress.kubernetes.io/proxy-send-timeout" = "3600"
        "nginx.ingress.kubernetes.io/proxy-http-version" = "1.1"
        "nginx.ingress.kubernetes.io/use-regex"          = "true"
      },
      local.use_rewrite ? {
        "nginx.ingress.kubernetes.io/rewrite-target" = "/$2"
      } : {}
    )
  }

  spec {
    ingress_class_name = "nginx"

    rule {
      host = local.ingress_host

      http {
        path {
          path      = "${var.ingress_path}(/|$)(.*)"
          path_type = "ImplementationSpecific"

          backend {
            service {
              name = local.service_name
              port {
                number = 80
              }
            }
          }
        }
      }
    }
  }
}
