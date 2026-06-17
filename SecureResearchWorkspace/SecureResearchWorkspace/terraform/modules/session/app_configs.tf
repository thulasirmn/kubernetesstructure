locals {
  app_type_lower = lower(var.app_type)

  # Whether nginx should rewrite the path prefix away before forwarding to the app.
  # RStudio: rewrite /$2 — the app serves from root but is told its public path via
  #   www-root-path in rserver.conf (set in base_command below).
  # Jupyter / Custom: no rewrite — the app sets its own base_url and generates
  #   URLs that already include the /s/<slug>/ prefix.
  use_rewrite = local.app_type_lower == "rstudio"

  # ── Command per app type ────────────────────────────────────────────────────
  #
  # Add a new application type by adding a key here.
  # The command list must result in the container serving on var.container_port.
  #
  base_command = (
    local.app_type_lower == "jupyter"
    ? (length(jsondecode(var.command_json)) > 0
      ? [for c in jsondecode(var.command_json) : replace(c, "__BASE_URL__", "${var.ingress_path}/")]
      : [
          "start-notebook.sh",
          "--NotebookApp.token=''",
          "--NotebookApp.password=''",
          "--NotebookApp.base_url=${var.ingress_path}/"
        ]
      )
    : local.app_type_lower == "rstudio"
    ? [
        "/bin/bash", "-c",
        join(" ", [
          "printf '%s\\n' 'www-root-path=${var.ingress_path}' >> /etc/rstudio/rserver.conf 2>/dev/null || true;",
          "if [ -f /etc/rstudio/disable_auth_rserver.conf ]; then",
          "  printf '%s\\n' 'www-root-path=${var.ingress_path}' >> /etc/rstudio/disable_auth_rserver.conf;",
          "fi;",
          "exec /init"
        ])
      ]
    : length(jsondecode(var.command_json)) > 0
    ? [for c in jsondecode(var.command_json) : replace(c, "__BASE_URL__", "${var.ingress_path}/")]
    : []
  )

  # ── Environment variables ───────────────────────────────────────────────────
  base_env = (
    var.environment_json != "" && var.environment_json != "{}"
    ? jsondecode(var.environment_json)
    : {}
  )

  # ── Ingress ─────────────────────────────────────────────────────────────────
  is_ip_domain = can(regex("^[0-9]+\\.[0-9]+\\.[0-9]+\\.[0-9]+$", var.ingress_domain))
  ingress_host = local.is_ip_domain ? null : var.ingress_domain
  access_url   = (local.is_ip_domain
    ? "http://${var.ingress_domain}${var.ingress_path}/"
    : "https://${var.ingress_domain}${var.ingress_path}/"
  )

  # ── K8s resource names ──────────────────────────────────────────────────────
  deployment_name = "sess-${substr(var.session_id, 0, 8)}"
  service_name    = "svc-${substr(var.session_id, 0, 8)}"
  sanitized_user  = replace(lower(var.user_id), "/[^a-z0-9._-]/", "-")

  labels = {
    "srw.io/session-id"   = var.session_id
    "srw.io/workspace-id" = var.workspace_id
    "srw.io/user-id"      = local.sanitized_user
    "srw.io/app"          = local.app_type_lower
    "app"                 = local.deployment_name
  }
}
