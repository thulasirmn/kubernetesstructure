output "k8s_namespace" {
  value = kubernetes_namespace.workspace.metadata[0].name
}
