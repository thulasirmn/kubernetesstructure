# SRW Implementation Plan - Native .docx Generator
# Creates a proper Open XML Word document without requiring Microsoft Word.

$outputPath = "D:\kubernetesstructure\SecureResearchWorkspace\SecureResearchWorkspace\docs\SRW-Implementation-Plan.docx"
$tempDir    = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "srw-docx-$(Get-Random)")

New-Item -ItemType Directory -Force -Path "$tempDir\_rels"      | Out-Null
New-Item -ItemType Directory -Force -Path "$tempDir\word\_rels" | Out-Null

# ── XML escape ────────────────────────────────────────────────────────────────
function XE($t) {
    if ($null -eq $t) { return "" }
    $t.ToString() -replace '&','&amp;' -replace '<','&lt;' -replace '>','&gt;' -replace '"','&quot;'
}

# ── Paragraph builders ────────────────────────────────────────────────────────
function WH1($t) { "<w:p><w:pPr><w:pStyle w:val='Heading1'/></w:pPr><w:r><w:t xml:space='preserve'>$(XE $t)</w:t></w:r></w:p>" }
function WH2($t) { "<w:p><w:pPr><w:pStyle w:val='Heading2'/></w:pPr><w:r><w:t xml:space='preserve'>$(XE $t)</w:t></w:r></w:p>" }
function WH3($t) { "<w:p><w:pPr><w:pStyle w:val='Heading3'/></w:pPr><w:r><w:t xml:space='preserve'>$(XE $t)</w:t></w:r></w:p>" }
function WP($t)  { "<w:p><w:r><w:t xml:space='preserve'>$(XE $t)</w:t></w:r></w:p>" }
function WPEmpty()    { "<w:p/>" }
function WPageBreak() { "<w:p><w:r><w:br w:type='page'/></w:r></w:p>" }

function WCover($title, $sub1, $sub2, [string[]]$meta) {
    $lines = $meta | ForEach-Object {
        "<w:p><w:pPr><w:jc w:val='center'/><w:spacing w:after='60'/></w:pPr><w:r><w:t xml:space='preserve'>$(XE $_)</w:t></w:r></w:p>"
    }
    "<w:p><w:pPr><w:jc w:val='center'/><w:spacing w:before='1440' w:after='240'/></w:pPr>" +
    "<w:r><w:rPr><w:b/><w:color w:val='0070C0'/><w:sz w:val='56'/><w:szCs w:val='56'/></w:rPr>" +
    "<w:t>$(XE $title)</w:t></w:r></w:p>" +
    "<w:p><w:pPr><w:jc w:val='center'/><w:spacing w:after='120'/></w:pPr>" +
    "<w:r><w:rPr><w:color w:val='444444'/><w:sz w:val='32'/><w:szCs w:val='32'/></w:rPr>" +
    "<w:t>$(XE $sub1)</w:t></w:r></w:p>" +
    "<w:p><w:pPr><w:jc w:val='center'/><w:spacing w:after='720'/></w:pPr>" +
    "<w:r><w:rPr><w:color w:val='444444'/><w:sz w:val='28'/><w:szCs w:val='28'/></w:rPr>" +
    "<w:t>$(XE $sub2)</w:t></w:r></w:p>" +
    ($lines -join '')
}

function WBullet($t, $lvl=0) {
    "<w:p><w:pPr><w:numPr><w:ilvl w:val='$lvl'/><w:numId w:val='1'/></w:numPr></w:pPr>" +
    "<w:r><w:t xml:space='preserve'>$(XE $t)</w:t></w:r></w:p>"
}

function WCode($t) {
    "<w:p><w:pPr><w:pStyle w:val='CodeBlock'/></w:pPr><w:r><w:t xml:space='preserve'>$(XE $t)</w:t></w:r></w:p>"
}

function WCallout($t, $type="info") {
    $fill   = @{info="EBF3FB"; warn="FFF4CE"; risk="FDE7E9"}[$type]
    $border = @{info="0070C0"; warn="F2A900"; risk="C50F1F"}[$type]
    "<w:tbl><w:tblPr><w:tblW w:w='5000' w:type='pct'/>" +
    "<w:tblBorders>" +
    "<w:top    w:val='single' w:sz='4'  w:color='$border'/>" +
    "<w:left   w:val='single' w:sz='24' w:color='$border'/>" +
    "<w:bottom w:val='single' w:sz='4'  w:color='$border'/>" +
    "<w:right  w:val='single' w:sz='4'  w:color='$border'/>" +
    "</w:tblBorders>" +
    "<w:tblCellMar><w:top w:w='80' w:type='dxa'/><w:left w:w='144' w:type='dxa'/>" +
    "<w:bottom w:w='80' w:type='dxa'/><w:right w:w='144' w:type='dxa'/></w:tblCellMar>" +
    "</w:tblPr><w:tr><w:tc>" +
    "<w:tcPr><w:shd w:val='clear' w:color='auto' w:fill='$fill'/></w:tcPr>" +
    "<w:p><w:r><w:t xml:space='preserve'>$(XE $t)</w:t></w:r></w:p>" +
    "</w:tc></w:tr></w:tbl>"
}

function WPhase($num, $title, $hrs, $goal, [string[]]$tasks, $exit) {
    $taskXml = $tasks | ForEach-Object {
        "<w:p><w:pPr><w:numPr><w:ilvl w:val='0'/><w:numId w:val='1'/></w:numPr></w:pPr>" +
        "<w:r><w:t xml:space='preserve'>$(XE $_)</w:t></w:r></w:p>"
    }
    "<w:tbl><w:tblPr><w:tblW w:w='5000' w:type='pct'/>" +
    "<w:tblBorders>" +
    "<w:top    w:val='single' w:sz='8' w:color='0070C0'/>" +
    "<w:left   w:val='single' w:sz='8' w:color='0070C0'/>" +
    "<w:bottom w:val='single' w:sz='8' w:color='0070C0'/>" +
    "<w:right  w:val='single' w:sz='8' w:color='0070C0'/>" +
    "<w:insideH w:val='single' w:sz='4' w:color='D0D7E2'/>" +
    "</w:tblBorders></w:tblPr>" +
    "<w:tr><w:tc><w:tcPr><w:shd w:val='clear' w:color='auto' w:fill='0070C0'/></w:tcPr>" +
    "<w:p><w:pPr><w:spacing w:after='60'/></w:pPr>" +
    "<w:r><w:rPr><w:b/><w:color w:val='FFFFFF'/><w:sz w:val='24'/></w:rPr>" +
    "<w:t>Phase $num  |  $(XE $title)  |  ~$hrs hours</w:t></w:r></w:p>" +
    "</w:tc></w:tr>" +
    "<w:tr><w:tc>" +
    "<w:p><w:r><w:rPr><w:b/></w:rPr><w:t xml:space='preserve'>Goal: </w:t></w:r>" +
    "<w:r><w:t xml:space='preserve'>$(XE $goal)</w:t></w:r></w:p>" +
    ($taskXml -join '') +
    "<w:p><w:r><w:rPr><w:b/></w:rPr><w:t xml:space='preserve'>Exit criteria: </w:t></w:r>" +
    "<w:r><w:t xml:space='preserve'>$(XE $exit)</w:t></w:r></w:p>" +
    "</w:tc></w:tr></w:tbl>"
}

# Table helpers
function WTblOpen() {
    "<w:tbl><w:tblPr><w:tblW w:w='5000' w:type='pct'/>" +
    "<w:tblBorders>" +
    "<w:top     w:val='single' w:sz='4' w:color='D0D7E2'/>" +
    "<w:left    w:val='single' w:sz='4' w:color='D0D7E2'/>" +
    "<w:bottom  w:val='single' w:sz='4' w:color='D0D7E2'/>" +
    "<w:right   w:val='single' w:sz='4' w:color='D0D7E2'/>" +
    "<w:insideH w:val='single' w:sz='4' w:color='D0D7E2'/>" +
    "<w:insideV w:val='single' w:sz='4' w:color='D0D7E2'/>" +
    "</w:tblBorders></w:tblPr>"
}
function WTblClose() { "</w:tbl>" }

function WTHR([string[]]$cells) {
    $tds = $cells | ForEach-Object {
        "<w:tc><w:tcPr><w:shd w:val='clear' w:color='auto' w:fill='0070C0'/></w:tcPr>" +
        "<w:p><w:r><w:rPr><w:b/><w:color w:val='FFFFFF'/></w:rPr>" +
        "<w:t xml:space='preserve'>$(XE $_)</w:t></w:r></w:p></w:tc>"
    }
    "<w:tr>$($tds -join '')</w:tr>"
}
function WTDR([string[]]$cells, [bool]$shade=$false) {
    $fill = if ($shade) { "F5F9FE" } else { "FFFFFF" }
    $tds = $cells | ForEach-Object {
        "<w:tc><w:tcPr><w:shd w:val='clear' w:color='auto' w:fill='$fill'/></w:tcPr>" +
        "<w:p><w:r><w:t xml:space='preserve'>$(XE $_)</w:t></w:r></w:p></w:tc>"
    }
    "<w:tr>$($tds -join '')</w:tr>"
}

# ─────────────────────────────────────────────────────────────────────────────
# BUILD DOCUMENT BODY
# ─────────────────────────────────────────────────────────────────────────────
$body = [System.Text.StringBuilder]::new()
function Add($xml) { $body.AppendLine($xml) | Out-Null }

# COVER PAGE
Add (WCover `
    "Secure Research Workspace" `
    "Workspace Application Launch" `
    "Implementation Plan and Architecture" `
    @(
        "CONFIDENTIAL  |  ARCHITECTURE DOCUMENT",
        "",
        "Platform:        Azure TRE + Azure Kubernetes Service (AKS)",
        "Document Type:   Technical Architecture and Implementation Plan",
        "Audience:        Senior Principal Lead / Engineering Leadership",
        "Status:          Ready for Review",
        "Version:         1.0",
        "Date:            June 2026"
    )
)
Add (WPageBreak)

# SECTION 1 - EXECUTIVE SUMMARY
Add (WH1 "1.  Executive Summary")
Add (WP  "The Secure Research Workspace (SRW) platform is built on top of Azure Trusted Research Environment (Azure TRE), which today provisions isolated Azure Storage Accounts and File Shares for each research workspace. This document describes the plan to extend the platform so that researchers can launch, access, and manage containerised analytical applications (Jupyter Notebook, RStudio, and custom Docker applications) directly within their provisioned workspace from a React-based browser UI.")
Add (WPEmpty)
Add (WCallout "Scope in one sentence: Extend the existing TRE workspace application so that every researcher assigned to a workspace can click an application icon in the browser, have a personal containerised environment spin up on AKS within minutes, and access it via a browser URL with full data isolation, async provisioning, and clean lifecycle management." "info")
Add (WPEmpty)
Add (WP "The implementation spans five layers:")
Add (WBullet "Terraform infrastructure - AKS namespace provisioning (new) and per-session Kubernetes resource management")
Add (WBullet "Backend API - ASP.NET Core Minimal API with Clean Architecture; async session launch pattern (202 Accepted)")
Add (WBullet "Background workers - Terraform orchestration, K8s status polling, session stop, idle cleanup")
Add (WBullet "React UI - application gallery, click-to-launch flow, session status polling, stop flow")
Add (WBullet "End-to-end testing - live Jupyter, RStudio, storage isolation, session stop flows")
Add (WPEmpty)
Add (WTblOpen)
Add (WTHR @("Metric","Value"))
Add (WTDR @("Total ADO Tasks","28 tasks") $false)
Add (WTDR @("Total Estimated Effort","82 hours") $true)
Add (WTDR @("Story Points","26") $false)
Add (WTDR @("Implementation Phases","5 sequential phases") $true)
Add (WTDR @("Application Types Supported","Jupyter Notebook, RStudio, Custom Docker") $false)
Add (WTDR @("Infrastructure Backend","Terraform CLI (azurerm + kubernetes providers)") $true)
Add (WTDR @("AKS Ingress","nginx-ingress with path-based routing per session") $false)
Add (WTblClose)
Add (WPageBreak)

# SECTION 2 - CURRENT STATE
Add (WH1 "2.  Current State and Integration Context")
Add (WH2 "2.1  What Azure TRE Provides Today")
Add (WP "Azure TRE handles the Azure-plane workspace boundary. The resources below are provisioned by TRE and are NOT in scope for this implementation:")
Add (WPEmpty)
Add (WTblOpen)
Add (WTHR @("Resource","Naming Convention","Provisioned By"))
Add (WTDR @("Azure Storage Account","srw<guid>","Azure TRE") $false)
Add (WTDR @("Azure File Share (SMB)","workspace-share","Azure TRE") $true)
Add (WTDR @("Storage Account Key","Available as TRE output","Azure TRE") $false)
Add (WTblClose)
Add (WPEmpty)
Add (WH2 "2.2  What This Implementation Adds (New Work)")
Add (WP "TRE does not manage Kubernetes resources. The AKS namespace, network isolation, the K8s Secret binding TRE storage keys to the CSI driver, and all per-session K8s objects must be created and managed by this platform layer.")
Add (WPEmpty)
Add (WTblOpen)
Add (WTHR @("Capability","Status"))
Add (WTDR @("AKS namespace per workspace","NEW - this implementation") $false)
Add (WTDR @("K8s Secret for Azure File CSI driver (azure-storage-creds)","NEW - this implementation") $true)
Add (WTDR @("Default-deny NetworkPolicy (workspace isolation)","NEW - this implementation") $false)
Add (WTDR @("Application catalogue (WorkspaceApplication entity)","NEW - this implementation") $true)
Add (WTDR @("Per-user session K8s Deployment + Service + Ingress","NEW - this implementation") $false)
Add (WTDR @("Per-user storage isolation (CSI sub_path)","NEW - this implementation") $true)
Add (WTDR @("Async session API (202 Accepted pattern + status polling)","NEW - this implementation") $false)
Add (WTDR @("React UI click-to-launch flow","NEW - this implementation") $true)
Add (WTblClose)
Add (WPageBreak)

# SECTION 3 - ARCHITECTURE
Add (WH1 "3.  Solution Architecture")
Add (WH2 "3.1  Architecture Overview")
Add (WCode "[ Researcher Browser ]")
Add (WCode "    React UI: Workspace page -> AppCard gallery -> Click to launch")
Add (WCode "         | HTTPS  (X-User-Id header)")
Add (WCode "         v")
Add (WCode "[ SRW API  (ASP.NET Core - AKS pod) ]")
Add (WCode "    POST /sessions  -> SessionLauncher -> ISessionProvisioningQueue")
Add (WCode "    GET  /sessions/{id} <- Cosmos DB (live status)")
Add (WCode "    DELETE /sessions/{id} -> Service Bus (session-stop queue)")
Add (WCode "         |                                   |")
Add (WCode " [ SessionLaunchWorker ]         [ SessionStopConsumer ]")
Add (WCode "   Channel<T> queue                 Service Bus consumer")
Add (WCode "   terraform apply                  terraform destroy")
Add (WCode "         |                                   |")
Add (WCode "         v                                   v")
Add (WCode "[ AKS Cluster  (namespace: ws-<name>-<id>) ]")
Add (WCode "    kubernetes_deployment  sess-<8hex>")
Add (WCode "      container: Jupyter / RStudio / Custom")
Add (WCode "        VolumeMount: workspace-share / sub_path=userId")
Add (WCode "    kubernetes_service     svc-<8hex>   (ClusterIP)")
Add (WCode "    kubernetes_ingress_v1  /s/<slug>/   (nginx)")
Add (WCode "    Secret: azure-storage-creds  <-- TRE Storage Key")
Add (WCode "    NetworkPolicy: default-deny (ingress-nginx only)")
Add (WCode "         |")
Add (WCode "         v")
Add (WCode "[ Azure Storage - provisioned by TRE ]")
Add (WCode "    Storage Account: srw<guid>")
Add (WCode "    File Share: workspace-share")
Add (WCode "      /alice/   <- sub_path for user alice")
Add (WCode "      /bob/     <- sub_path for user bob")
Add (WPEmpty)

Add (WH2 "3.2  Clean Architecture Layers")
Add (WP "Dependencies flow inward only. No outer layer may reference an inner layer concrete types.")
Add (WPEmpty)
Add (WTblOpen)
Add (WTHR @("Layer","Project","Responsibility"))
Add (WTDR @("Domain","SRW.Domain","Entities only: WorkspaceApplication, UserSession, SessionStatus enum. No external dependencies.") $false)
Add (WTDR @("Core","SRW.Core","Application services (SessionLauncher) and interface abstractions (ISessionProvisioningQueue, IKubernetesOrchestrator, ISessionRepository).") $true)
Add (WTDR @("Infrastructure","SRW.Infrastructure","Implements interfaces: TerraformOrchestrator, background workers (SessionLaunchWorker, SessionStatusPoller, SessionStopConsumer, IdleSessionReaper), Cosmos DB repositories.") $false)
Add (WTDR @("API","SRW.Api","ASP.NET Core Minimal API endpoints (SessionEndpoints, ApplicationEndpoints), DI composition root (Program.cs), auth middleware.") $true)
Add (WTDR @("UI","React (existing app)","Session API client (sessionApi.ts), useSessionPoller hook, AppCard component, workspace applications page, active sessions panel.") $false)
Add (WTblClose)
Add (WPEmpty)

Add (WH2 "3.3  Kubernetes Resource Model")
Add (WP "Two tiers of K8s resources are managed, each backed by a separate Terraform state file in Azure Blob:")
Add (WPEmpty)
Add (WTblOpen)
Add (WTHR @("Tier","Resources Created","Lifecycle","Terraform State Key"))
Add (WTDR @("Workspace (once per workspace)","kubernetes_namespace + kubernetes_secret (azure-storage-creds) + kubernetes_network_policy","Created on workspace activation. Destroyed when workspace is deleted.","workspaces/{k8sNamespace}-k8s.tfstate") $false)
Add (WTDR @("Session (once per user per launch)","kubernetes_deployment (1 replica) + kubernetes_service (ClusterIP) + kubernetes_ingress_v1","Created on session launch. Destroyed on stop or idle reap.","sessions/{sessionId}.tfstate") $true)
Add (WTblClose)
Add (WPEmpty)
Add (WCallout "Security: The Azure Storage Account key (from TRE) is passed to Terraform exclusively as the environment variable TF_VAR_storage_account_key. It is never written to a .tfvars file on disk or stored in Terraform state in plaintext." "info")
Add (WPEmpty)

Add (WH2 "3.4  Session Lifecycle and Async Pattern")
Add (WP "Terraform provisioning takes 2-3 minutes per session. The API uses a non-blocking async pattern: the HTTP request returns in under 2 seconds, a background worker does the slow work, and the client polls for status.")
Add (WPEmpty)
Add (WCode "Researcher clicks Launch in UI")
Add (WCode "  |")
Add (WCode "  v")
Add (WCode "POST /api/workspaces/{id}/sessions")
Add (WCode "  |  (< 2 seconds)")
Add (WCode "  v")
Add (WCode "202 Accepted  { status: 'Starting', accessUrl: null }")
Add (WCode "  |")
Add (WCode "  v  SessionLaunchWorker (background, ~2-3 min)")
Add (WCode "  terraform init + apply")
Add (WCode "  -> K8s Deployment + Service + Ingress created")
Add (WCode "  -> session.DeploymentName, ServiceName, AccessUrl written to DB")
Add (WCode "  |")
Add (WCode "  v  SessionStatusPoller (every 5 seconds)")
Add (WCode "  GET K8s pod readyReplicas")
Add (WCode "  -> status updated to 'Running' when pod is ready")
Add (WCode "  |")
Add (WCode "  v")
Add (WCode "GET /sessions/{id} -> { status: 'Running', accessUrl: 'https://....' }")
Add (WPEmpty)
Add (WTblOpen)
Add (WTHR @("Status","Meaning","Set By"))
Add (WTDR @("Starting","Session created in DB; Terraform apply in progress","SessionLauncher (on enqueue)") $false)
Add (WTDR @("Running","Pod has >=1 ready replica; accessUrl is populated","SessionStatusPoller") $true)
Add (WTDR @("Stopping","DELETE called; teardown queued to Service Bus","SessionEndpoints DELETE handler") $false)
Add (WTDR @("Stopped","K8s resources destroyed; session ended","SessionStopConsumer") $true)
Add (WTDR @("Failed","Terraform apply threw an exception","SessionLaunchWorker catch block") $false)
Add (WTblClose)
Add (WPEmpty)

Add (WH2 "3.5  Per-User Storage Isolation")
Add (WP "All researchers share one Azure File Share (workspace-share). Isolation is enforced at the CSI driver volume mount level using sub_path. The sub_path value is the sanitised user ID (characters outside [a-z0-9._-] replaced with '-'). The per-user directory is created automatically on first mount.")
Add (WPEmpty)
Add (WCode "workspace-share  (Azure File Share - TRE provisioned)")
Add (WCode "  /alice-research/        <- sub_path for user 'alice-research'")
Add (WCode "    my_notebook.ipynb")
Add (WCode "  /bob-dev/               <- sub_path for user 'bob-dev'")
Add (WCode "    analysis.R")
Add (WCode "")
Add (WCode "Each pod mounts only its own sub_path directory.")
Add (WCode "Users cannot navigate above their mount point.")
Add (WPEmpty)

Add (WH2 "3.6  React UI Session State Machine")
Add (WTblOpen)
Add (WTHR @("UI State","Visual","User Action Available"))
Add (WTDR @("Idle","App icon + name + Launch button","Click Launch") $false)
Add (WTDR @("Launching","Spinner overlay, Requesting...","None - button disabled") $true)
Add (WTDR @("Starting","Spinner overlay, Starting... (may take a few minutes)","None - button disabled") $false)
Add (WTDR @("Running","Green active badge, Open button, Stop button","Open in new tab  /  Stop") $true)
Add (WTDR @("Stopping","Spinner overlay, Stopping...","None") $false)
Add (WTDR @("Failed","Red error badge + error message","Retry") $true)
Add (WTblClose)
Add (WPEmpty)
Add (WCallout "Idempotency: If a researcher clicks Launch while a session is already Starting or Running, the API returns the existing session. No duplicate deployment is created. The UI disables the button during Launching and Starting states as an additional safeguard." "info")
Add (WPageBreak)

# SECTION 4 - IMPLEMENTATION PLAN
Add (WH1 "4.  Implementation Plan")
Add (WP "The implementation is structured into five sequential phases. Each phase is independently deployable and testable.")
Add (WPEmpty)

Add (WPhase 1 "AKS Namespace Provisioning" 14 `
    "After Azure TRE provisions a workspace Storage Account and File Share, automatically create the matching AKS namespace with network isolation and the storage credential Secret needed by the CSI driver. This is the foundation for all subsequent session launches." `
    @(
        "T-01  Terraform workspace-k8s module: kubernetes_namespace + azure-storage-creds Secret + default-deny NetworkPolicy",
        "T-08  TerraformRunner: backend.tfbackend file, init-skip optimisation, process kill on CancellationToken",
        "T-09  TerraformOrchestrator.EnsureWorkspaceNamespaceAsync + DeleteWorkspaceNamespaceAsync"
    ) `
    "After workspace activation, kubectl get ns shows the workspace namespace with azure-storage-creds Secret and default-deny NetworkPolicy in place."
)
Add (WPEmpty)

Add (WPhase 2 "Application Catalogue and Domain Layer" 8 `
    "Introduce the WorkspaceApplication and UserSession domain entities, the ISessionProvisioningQueue interface, the SessionLauncher service, and the ApplicationEndpoints API so workspace admins can register launchable applications." `
    @(
        "T-04  WorkspaceApplication entity (type, image, resource limits, mount path)",
        "T-05  UserSession entity + SessionStatus enum (Pending/Starting/Running/Stopping/Stopped/Failed)",
        "T-06  ISessionProvisioningQueue interface in SRW.Core.Abstractions",
        "T-07  SessionLauncher: validate -> persist Starting session -> enqueue -> return immediately",
        "T-15  ApplicationEndpoints: POST / GET applications per workspace"
    ) `
    "Workspace admin can register a Jupyter application via POST /api/workspaces/{id}/applications and retrieve it."
)
Add (WPEmpty)

Add (WPhase 3 "Session Launch Backend" 19 `
    "Deliver the complete backend session lifecycle - launch (async), K8s status polling, session stop, and idle cleanup - backed by the session Terraform module with per-user storage isolation." `
    @(
        "T-02  Terraform session module: Deployment + Service + Ingress + per-user sub_path isolation",
        "T-03  Terraform app_configs.tf: Jupyter / RStudio / Custom startup logic and ingress rewrite rules",
        "T-10  TerraformOrchestrator.LaunchSessionAsync + StopSessionAsync + GetSessionStatusAsync",
        "T-11  SessionLaunchWorker: background Channel<T> worker, DI scope per item",
        "T-12  SessionStatusPoller: K8s pod readiness -> Running transition (every 5 s)",
        "T-13  SessionStopConsumer: Service Bus -> terraform destroy -> Stopped",
        "T-14  IdleSessionReaper: auto-stop sessions beyond configurable idle threshold",
        "T-16  SessionEndpoints: POST / GET / DELETE sessions",
        "T-17  Program.cs: DI registration for all new services"
    ) `
    "POST /sessions returns 202 in under 2 s; GET /sessions/{id} transitions to Running once the pod is ready; DELETE tears down all K8s resources cleanly."
)
Add (WPEmpty)

Add (WPhase 4 "React UI" 21 `
    "Integrate the session API into the existing React workspace application so researchers experience a seamless click-to-launch flow with live status feedback, persistent session state on page return, and a stop flow with confirmation." `
    @(
        "T-22  sessionApi.ts: typed fetch wrappers for launchSession, getSession, listSessions, stopSession",
        "T-23  useSessionPoller hook: polls GET /sessions/{id} at interval, stops on terminal state, cleans up on unmount",
        "T-24  AppCard component: 6 visual states (Idle / Launching / Starting / Running / Stopping / Failed)",
        "T-25  Workspace applications page: gallery of AppCards, parallel load of applications + existing sessions on mount",
        "T-26  Session launch flow: click handler state machine POST -> Starting -> Running / Failed",
        "T-27  Active sessions panel: list of Running sessions with Open and Stop actions",
        "T-28  Session stop flow: confirmation dialog -> DELETE -> card reset to Idle"
    ) `
    "Researcher clicks a Jupyter icon, sees Starting... feedback, and the application opens in a new tab when ready. Running sessions persist across page reloads without flashing Idle."
)
Add (WPEmpty)

Add (WPhase 5 "End-to-End Testing" 12 `
    "Verify all flows against a real AKS cluster and TRE-provisioned workspace before sign-off." `
    @(
        "T-18  E2E: Jupyter session launch, browser access, file persistence to user subdirectory",
        "T-19  E2E: RStudio session, path prefix correctness, terminal WebSocket, Files pane",
        "T-20  E2E: Per-user storage isolation - two researchers, same workspace, cannot access each other data",
        "T-21  E2E: Session stop (DELETE) and idle reaper automatic cleanup"
    ) `
    "All four E2E scenarios pass against a live cluster. No orphaned K8s resources after stop. Storage isolation confirmed with two concurrent users."
)
Add (WPageBreak)

# SECTION 5 - WORK ITEMS
Add (WH1 "5.  Work Item Summary (28 ADO Tasks, 82 hours)")
Add (WTblOpen)
Add (WTHR @("#","Phase","Area","Title","Hours"))
Add (WTDR @("T-01","1","Terraform","workspace-k8s module - Namespace + CSI Secret + NetworkPolicy","4 h") $false)
Add (WTDR @("T-02","3","Terraform","session module - K8s Deployment + Service + Ingress","6 h") $true)
Add (WTDR @("T-03","3","Terraform","session module - app-type startup logic (Jupyter / RStudio / Custom)","4 h") $false)
Add (WTDR @("T-04","2","Domain","WorkspaceApplication entity","2 h") $true)
Add (WTDR @("T-05","2","Domain","UserSession entity + SessionStatus enum","2 h") $false)
Add (WTDR @("T-06","2","Core","ISessionProvisioningQueue interface","1 h") $true)
Add (WTDR @("T-07","2","Core","SessionLauncher - validate, persist, enqueue, return fast","3 h") $false)
Add (WTDR @("T-08","1","Infrastructure","TerraformRunner - backend file, init-skip, process kill on cancel","4 h") $true)
Add (WTDR @("T-09","1","Infrastructure","TerraformOrchestrator - EnsureWorkspaceNamespaceAsync","3 h") $false)
Add (WTDR @("T-10","3","Infrastructure","TerraformOrchestrator - LaunchSessionAsync + StopSessionAsync","4 h") $true)
Add (WTDR @("T-11","3","Infrastructure","SessionLaunchWorker - background channel worker","4 h") $false)
Add (WTDR @("T-12","3","Infrastructure","SessionStatusPoller - K8s pod readiness -> Running","3 h") $true)
Add (WTDR @("T-13","3","Infrastructure","SessionStopConsumer - Service Bus -> terraform destroy","3 h") $false)
Add (WTDR @("T-14","3","Infrastructure","IdleSessionReaper - idle threshold cleanup","2 h") $true)
Add (WTDR @("T-15","2","API","ApplicationEndpoints - register + list applications","2 h") $false)
Add (WTDR @("T-16","3","API","SessionEndpoints - POST / GET / DELETE sessions","3 h") $true)
Add (WTDR @("T-17","3","API","Program.cs - DI registration for all new services","2 h") $false)
Add (WTDR @("T-18","5","Test","E2E - Jupyter session launch, browser access, file persistence","3 h") $true)
Add (WTDR @("T-19","5","Test","E2E - RStudio session, path prefix, UI correctness","2 h") $false)
Add (WTDR @("T-20","5","Test","E2E - Per-user storage isolation (two users, same workspace)","2 h") $true)
Add (WTDR @("T-21","5","Test","E2E - Session stop and idle reaper cleanup","2 h") $false)
Add (WTDR @("T-22","4","UI","sessionApi.ts - typed fetch wrappers for POST / GET / DELETE","2 h") $true)
Add (WTDR @("T-23","4","UI","useSessionPoller hook - polls until terminal state","3 h") $false)
Add (WTDR @("T-24","4","UI","AppCard component - app icon with session status overlay","4 h") $true)
Add (WTDR @("T-25","4","UI","Workspace applications page - gallery + load existing sessions","3 h") $false)
Add (WTDR @("T-26","4","UI","Session launch flow - click handler state machine","3 h") $true)
Add (WTDR @("T-27","4","UI","Active sessions panel - Running sessions with Open / Stop","3 h") $false)
Add (WTDR @("T-28","4","UI","Session stop flow - confirmation dialog, DELETE, card reset","3 h") $true)
Add (WTDR @("","","","TOTAL","82 h") $false)
Add (WTblClose)
Add (WPageBreak)

# SECTION 6 - KEY DECISIONS
Add (WH1 "6.  Key Technical Decisions")
Add (WH2 "6.1  Terraform as the Infrastructure Backend")
Add (WP "All infrastructure operations are declared as Terraform modules (azurerm + kubernetes providers). The API shells out to the Terraform CLI rather than using imperative Azure SDK or Kubernetes client calls for provisioning.")
Add (WPEmpty)
Add (WTblOpen)
Add (WTHR @("Benefit","Detail"))
Add (WTDR @("Declarative drift detection","Terraform state tracks what was created; re-applying is idempotent") $false)
Add (WTDR @("Symmetric destroy","terraform destroy removes exactly what apply created - no manual cleanup code") $true)
Add (WTDR @("Remote state isolation","Azure Blob backend; each workspace/session has its own .tfstate file - no state contention") $false)
Add (WTDR @("Init-skip optimisation","terraform init skipped if .terraform/ already exists, saving 20-60 s per repeated operation") $true)
Add (WTblClose)
Add (WPEmpty)

Add (WH2 "6.2  Async Session Launch (202 Accepted Pattern)")
Add (WP "The HTTP handler does only synchronous work: validate, persist Starting session, enqueue. Returns 202 Accepted in under 2 seconds. SessionLaunchWorker (in-process Channel<T>) dequeues and runs Terraform in the background. SessionStatusPoller polls K8s pod readiness every 5 seconds and updates the session to Running. Client polls GET /sessions/{id} until Running or Failed.")
Add (WPEmpty)

Add (WH2 "6.3  In-Process Channel vs. Service Bus")
Add (WTblOpen)
Add (WTHR @("","Session Launch","Session Stop"))
Add (WTDR @("Mechanism","In-process Channel<T>","Azure Service Bus") $false)
Add (WTDR @("Reason","Initiated from the same process; sub-ms enqueue; no durability needed","Must survive API pod restart mid-teardown; provides at-least-once delivery and dead-lettering") $true)
Add (WTblClose)
Add (WPEmpty)

Add (WH2 "6.4  ISessionProvisioningQueue Defined in Core")
Add (WP "SessionLauncher (Core layer) must not reference Infrastructure types. Defining ISessionProvisioningQueue in SRW.Core.Abstractions keeps the Clean Architecture dependency arrow pointing inward. SessionLaunchWorker (Infrastructure) implements it.")
Add (WPEmpty)

Add (WH2 "6.5  Ingress Path Routing: Jupyter vs. RStudio")
Add (WTblOpen)
Add (WTHR @("App Type","Nginx Behaviour","Why"))
Add (WTDR @("Jupyter","No rewrite - path passed through","Jupyter accepts --NotebookApp.base_url at startup and generates all URLs with the prefix included") $false)
Add (WTDR @("RStudio","rewrite-target strips prefix","RStudio has no base-url flag; www-root-path is injected into rserver.conf before startup; nginx must strip the prefix") $true)
Add (WTDR @("Custom","No rewrite (default)","Supports __BASE_URL__ token substitution in commandJson at Terraform apply time") $false)
Add (WTblClose)
Add (WPEmpty)

Add (WH2 "6.6  Deployment Name from Terraform Output")
Add (WP "UserSession.Create() pre-generates a name (e.g. sess-a7fca0c050, 10 hex chars). Terraform creates the actual resource with 8 hex chars. After LaunchSessionAsync, the worker overwrites session.DeploymentName and session.ServiceName with the values from Terraform outputs. TF outputs are the source of truth for resource names.")
Add (WPageBreak)

# SECTION 7 - RISKS
Add (WH1 "7.  Risks and Mitigations")
Add (WTblOpen)
Add (WTHR @("#","Risk","Likelihood","Impact","Mitigation"))
Add (WTDR @("R-01","Terraform apply duration (~2-3 min) frustrates researchers expecting instant launch","High","Medium","Async API (202 pattern) + UI progress indicator eliminates blocking wait. Terraform plugin cache and init-skip reduce cold-start.") $false)
Add (WTDR @("R-02","Storage account key exposed in Terraform state or working directory","Medium","High","Key passed exclusively as TF_VAR_storage_account_key env var - never written to .tfvars. Remote state in Azure Blob with RBAC-controlled access.") $true)
Add (WTDR @("R-03","Session stuck in Starting if API pod restarts mid-provisioning","Medium","Medium","IdleSessionReaper detects Starting sessions older than the configured threshold and marks them Failed, allowing the researcher to retry.") $false)
Add (WTDR @("R-04","Per-user storage isolation failure - users accessing each other data","Low","High","sub_path enforced at the CSI driver / OS level. E2E test T-20 explicitly verifies isolation with two concurrent users before sign-off.") $true)
Add (WTDR @("R-05","nginx-ingress configuration-snippet annotation blocked by cluster admission webhook","Medium","Low","This implementation uses only standard annotations (rewrite-target, proxy-read-timeout) - no configuration-snippet annotation required.") $false)
Add (WTDR @("R-06","Terraform state contention if two launch requests fire simultaneously","Low","Medium","Idempotency check in SessionLauncher returns the existing session before enqueueing. Single-reader channel ensures one worker per session ID. Azure Blob backend provides state locking.") $true)
Add (WTDR @("R-07","Data Protection key ring is machine-local - new API pod cannot decrypt existing secrets","Medium","Medium","Migrate Data Protection key ring to Azure Blob + Key Vault before scaling API to multiple replicas. Documented as a follow-on hardening item.") $false)
Add (WTblClose)
Add (WPageBreak)

# SECTION 8 - DEPENDENCIES
Add (WH1 "8.  Dependencies and Prerequisites")
Add (WH2 "8.1  Platform Prerequisites (One-Time Cluster Setup)")
Add (WTblOpen)
Add (WTHR @("Prerequisite","Notes"))
Add (WTDR @("nginx-ingress installed on AKS cluster","Required for path-based session routing. Install via k8s/manifests/00-cluster-setup.yaml.") $false)
Add (WTDR @("Azure File CSI driver installed on AKS cluster","Required for SMB file share mounts. Enabled by default on AKS 1.21+.") $true)
Add (WTDR @("Azure Blob container for Terraform remote state","Named container (e.g. srw-tf-state) in a dedicated storage account.") $false)
Add (WTDR @("Terraform CLI binary accessible to the API process","Path configured via appsettings.json -> Terraform:TerraformBinaryPath.") $true)
Add (WTDR @("Azure RBAC for API managed identity","Contributor on workspace resource group; Storage Account Contributor; AKS RBAC to create namespaces and deployments.") $false)
Add (WTblClose)
Add (WPEmpty)
Add (WH2 "8.2  Per-Workspace Prerequisites (Provided by Azure TRE)")
Add (WTblOpen)
Add (WTHR @("Prerequisite","Provided By"))
Add (WTDR @("Azure Storage Account (srw<guid>)","Azure TRE") $false)
Add (WTDR @("Azure File Share (workspace-share)","Azure TRE") $true)
Add (WTDR @("Storage Account Key","Azure TRE output - passed as parameter to EnsureWorkspaceNamespaceAsync") $false)
Add (WTblClose)
Add (WPEmpty)
Add (WH2 "8.3  Developer Machine Prerequisites (Local Development)")
Add (WBullet ".NET 8 SDK")
Add (WBullet "Azure CLI (az login) - DefaultAzureCredential picks up local credentials")
Add (WBullet "kubectl + AKS kubeconfig (az aks get-credentials -n srw-aks-dev -g srw-dev-rg)")
Add (WBullet "Terraform CLI binary at path matching appsettings.Development.json -> Terraform:TerraformBinaryPath")
Add (WBullet "appsettings.Development.json created from the .example template with real values for Cosmos DB, Service Bus, Terraform config")
Add (WBullet "Node.js + npm (for React UI development)")
Add (WBullet "kubectl port-forward -n ingress-nginx svc/ingress-nginx-controller 80:80  (to open session URLs from local machine)")

# SECTION 9 - DEFINITION OF DONE
Add (WH1 "9.  Definition of Done")
Add (WP "The feature is considered complete when all of the following criteria are verified:")
Add (WPEmpty)
Add (WBullet "1.  All 28 ADO tasks completed and linked to the parent story")
Add (WBullet "2.  POST /sessions returns 202 Accepted in under 2 seconds for all application types")
Add (WBullet "3.  Jupyter session: launched end-to-end, reached Running, notebook accessible in browser, files persisted to user subdirectory")
Add (WBullet "4.  RStudio session: path prefix reflected correctly in UI, no broken asset URLs, terminal (WebSocket) functional")
Add (WBullet "5.  Storage isolation verified: two researchers in the same workspace cannot access each other mounted files")
Add (WBullet "6.  Session stop (DELETE) tears down all 3 K8s resources; session reaches Stopped in DB")
Add (WBullet "7.  Idle session reaper automatically stops sessions beyond the configured threshold")
Add (WBullet "8.  React UI: click-to-launch flow works end-to-end; returning researchers see correct Running/Starting state without a flash of Idle")
Add (WBullet "9.  Cancelling a request does not leave an orphaned terraform.exe process")
Add (WBullet "10. dotnet build is clean with zero errors and zero warnings")
Add (WBullet "11. Clean Architecture dependency rule not violated - SRW.Core has no project reference to SRW.Infrastructure")
Add (WBullet "12. AKS namespace teardown tested: deleting a workspace tears down the K8s namespace and CSI Secret")

# ─────────────────────────────────────────────────────────────────────────────
# WRITE XML PACKAGE FILES
# ─────────────────────────────────────────────────────────────────────────────
$ct = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' + "`n" +
'<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">' + "`n" +
'  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>' + "`n" +
'  <Default Extension="xml"  ContentType="application/xml"/>' + "`n" +
'  <Override PartName="/word/document.xml"  ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>' + "`n" +
'  <Override PartName="/word/styles.xml"    ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>' + "`n" +
'  <Override PartName="/word/settings.xml"  ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml"/>' + "`n" +
'  <Override PartName="/word/numbering.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml"/>' + "`n" +
'</Types>'
[System.IO.File]::WriteAllText("$tempDir\[Content_Types].xml", $ct, [System.Text.Encoding]::UTF8)

$rels = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' + "`n" +
'<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">' + "`n" +
'  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>' + "`n" +
'</Relationships>'
[System.IO.File]::WriteAllText("$tempDir\_rels\.rels", $rels, [System.Text.Encoding]::UTF8)

$docRels = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' + "`n" +
'<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">' + "`n" +
'  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"    Target="styles.xml"/>' + "`n" +
'  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings"  Target="settings.xml"/>' + "`n" +
'  <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering" Target="numbering.xml"/>' + "`n" +
'</Relationships>'
[System.IO.File]::WriteAllText("$tempDir\word\_rels\document.xml.rels", $docRels, [System.Text.Encoding]::UTF8)

$settings = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' + "`n" +
'<w:settings xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">' + "`n" +
'  <w:defaultTabStop w:val="720"/>' + "`n" +
'  <w:compat><w:compatSetting w:name="compatibilityMode" w:uri="http://schemas.microsoft.com/office/word" w:val="15"/></w:compat>' + "`n" +
'</w:settings>'
[System.IO.File]::WriteAllText("$tempDir\word\settings.xml", $settings, [System.Text.Encoding]::UTF8)

$numbering = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' + "`n" +
'<w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">' + "`n" +
'  <w:abstractNum w:abstractNumId="0">' + "`n" +
'    <w:lvl w:ilvl="0">' + "`n" +
'      <w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="&#x2022;"/><w:lvlJc w:val="left"/>' + "`n" +
'      <w:pPr><w:ind w:left="360" w:hanging="360"/></w:pPr>' + "`n" +
'    </w:lvl>' + "`n" +
'    <w:lvl w:ilvl="1">' + "`n" +
'      <w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="o"/><w:lvlJc w:val="left"/>' + "`n" +
'      <w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr>' + "`n" +
'    </w:lvl>' + "`n" +
'  </w:abstractNum>' + "`n" +
'  <w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>' + "`n" +
'</w:numbering>'
[System.IO.File]::WriteAllText("$tempDir\word\numbering.xml", $numbering, [System.Text.Encoding]::UTF8)

$styles = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' + "`n" +
'<w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">' + "`n" +
'  <w:docDefaults>' + "`n" +
'    <w:rPrDefault><w:rPr>' + "`n" +
'      <w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:cs="Calibri"/>' + "`n" +
'      <w:sz w:val="22"/><w:szCs w:val="22"/><w:lang w:val="en-GB"/>' + "`n" +
'    </w:rPr></w:rPrDefault>' + "`n" +
'    <w:pPrDefault><w:pPr><w:spacing w:after="160" w:line="276" w:lineRule="auto"/></w:pPr></w:pPrDefault>' + "`n" +
'  </w:docDefaults>' + "`n" +
'  <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>' + "`n" +
'  <w:style w:type="paragraph" w:styleId="Heading1">' + "`n" +
'    <w:name w:val="heading 1"/><w:basedOn w:val="Normal"/>' + "`n" +
'    <w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="480" w:after="160"/>' + "`n" +
'      <w:pBdr><w:bottom w:val="single" w:sz="6" w:space="1" w:color="0070C0"/></w:pBdr>' + "`n" +
'    </w:pPr>' + "`n" +
'    <w:rPr><w:rFonts w:ascii="Calibri Light" w:hAnsi="Calibri Light"/><w:b/><w:color w:val="0070C0"/><w:sz w:val="36"/><w:szCs w:val="36"/></w:rPr>' + "`n" +
'  </w:style>' + "`n" +
'  <w:style w:type="paragraph" w:styleId="Heading2">' + "`n" +
'    <w:name w:val="heading 2"/><w:basedOn w:val="Normal"/>' + "`n" +
'    <w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="360" w:after="120"/></w:pPr>' + "`n" +
'    <w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri"/><w:b/><w:color w:val="005A9E"/><w:sz w:val="28"/><w:szCs w:val="28"/></w:rPr>' + "`n" +
'  </w:style>' + "`n" +
'  <w:style w:type="paragraph" w:styleId="Heading3">' + "`n" +
'    <w:name w:val="heading 3"/><w:basedOn w:val="Normal"/>' + "`n" +
'    <w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="240" w:after="80"/></w:pPr>' + "`n" +
'    <w:rPr><w:b/><w:color w:val="1a1a1a"/><w:sz w:val="24"/><w:szCs w:val="24"/></w:rPr>' + "`n" +
'  </w:style>' + "`n" +
'  <w:style w:type="paragraph" w:styleId="CodeBlock">' + "`n" +
'    <w:name w:val="CodeBlock"/><w:basedOn w:val="Normal"/>' + "`n" +
'    <w:pPr>' + "`n" +
'      <w:spacing w:after="0" w:line="240" w:lineRule="auto"/>' + "`n" +
'      <w:shd w:val="clear" w:color="auto" w:fill="F5F5F5"/>' + "`n" +
'      <w:pBdr>' + "`n" +
'        <w:top    w:val="single" w:sz="4" w:color="CCCCCC"/>' + "`n" +
'        <w:left   w:val="single" w:sz="4" w:color="CCCCCC"/>' + "`n" +
'        <w:bottom w:val="single" w:sz="4" w:color="CCCCCC"/>' + "`n" +
'        <w:right  w:val="single" w:sz="4" w:color="CCCCCC"/>' + "`n" +
'      </w:pBdr>' + "`n" +
'      <w:ind w:left="180" w:right="180"/>' + "`n" +
'    </w:pPr>' + "`n" +
'    <w:rPr><w:rFonts w:ascii="Courier New" w:hAnsi="Courier New" w:cs="Courier New"/><w:sz w:val="18"/><w:szCs w:val="18"/><w:color w:val="333333"/></w:rPr>' + "`n" +
'  </w:style>' + "`n" +
'  <w:style w:type="table" w:styleId="TableGrid">' + "`n" +
'    <w:name w:val="Table Grid"/>' + "`n" +
'    <w:tblPr>' + "`n" +
'      <w:tblBorders>' + "`n" +
'        <w:top    w:val="single" w:sz="4" w:color="D0D7E2"/>' + "`n" +
'        <w:left   w:val="single" w:sz="4" w:color="D0D7E2"/>' + "`n" +
'        <w:bottom w:val="single" w:sz="4" w:color="D0D7E2"/>' + "`n" +
'        <w:right  w:val="single" w:sz="4" w:color="D0D7E2"/>' + "`n" +
'        <w:insideH w:val="single" w:sz="4" w:color="D0D7E2"/>' + "`n" +
'        <w:insideV w:val="single" w:sz="4" w:color="D0D7E2"/>' + "`n" +
'      </w:tblBorders>' + "`n" +
'      <w:tblCellMar>' + "`n" +
'        <w:top    w:w="80"  w:type="dxa"/>' + "`n" +
'        <w:left   w:w="108" w:type="dxa"/>' + "`n" +
'        <w:bottom w:w="80"  w:type="dxa"/>' + "`n" +
'        <w:right  w:w="108" w:type="dxa"/>' + "`n" +
'      </w:tblCellMar>' + "`n" +
'    </w:tblPr>' + "`n" +
'    <w:rPr><w:sz w:val="20"/><w:szCs w:val="20"/></w:rPr>' + "`n" +
'  </w:style>' + "`n" +
'</w:styles>'
[System.IO.File]::WriteAllText("$tempDir\word\styles.xml", $styles, [System.Text.Encoding]::UTF8)

# Build and write document.xml
$docXml = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' + "`n" +
'<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">' + "`n" +
'<w:body>' + "`n" +
$body.ToString() + "`n" +
'<w:sectPr>' +
'<w:pgSz w:w="12240" w:h="15840"/>' +
'<w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" w:header="720" w:footer="720" w:gutter="0"/>' +
'</w:sectPr>' + "`n" +
'</w:body></w:document>'
[System.IO.File]::WriteAllText("$tempDir\word\document.xml", $docXml, [System.Text.Encoding]::UTF8)

# Package into .docx (ZIP)
if (Test-Path $outputPath) { Remove-Item $outputPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($tempDir, $outputPath)
Remove-Item -Recurse -Force $tempDir

if (Test-Path $outputPath) {
    $kb = [math]::Round((Get-Item $outputPath).Length / 1KB, 1)
    Write-Host "SUCCESS: $outputPath  ($kb KB)"
} else {
    Write-Host "FAILED: file not found after packaging"
}
