# Deprecated — Terraform Removed

Terraform was **removed on 2026-06-18**. All provisioning now uses native SDKs and Helm:

- **Azure Storage Account + File Share** → Azure ARM SDK (`AzureStorageProvisioner`)
- **K8s Namespace + Secret + NetworkPolicy** → Kubernetes client SDK (`KubernetesOrchestrator`)
- **Session Deployment + Service + Ingress** → Helm CLI (`HelmRunner` + `charts/session/`)

See **[NEW_MACHINE_SETUP_HELM.md](NEW_MACHINE_SETUP_HELM.md)** for the current setup guide.
