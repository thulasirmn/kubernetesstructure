# Executive Summary — Moving Research Workspaces from TRE App Service to AKS

## The ask
Compare the cost of hosting research applications (Jupyter, RStudio, DBGate, FDSA) on the
current **Azure TRE model — one App Service Plan per workspace** — against the new **Secure
Research Workspace (SRW) platform on a shared AKS cluster**.

## Bottom line

> **~71% lower compute cost — roughly $114,000 saved per year** at 50 workspaces / 500 researchers,
> while giving each active researcher *more* compute and stronger isolation.

| | Per year | Per month |
|---|---|---|
| **TRE** — 50 dedicated App Service Plans (P2v3, 24×7) | **$160,300** | $13,359 |
| **SRW on AKS** — shared cluster, pods on demand | **$46,330** | $3,861 |
| **Annual saving** | **~$114,000** | $9,498 |

## Why it's cheaper — one idea

**TRE bills per workspace, 24×7. AKS bills per active session.**

- App Service Plans **cannot scale to zero** — all 50 plans run and bill around the clock,
  even overnight and on weekends when no one is logged in.
- AKS creates a pod **only when a researcher launches an app**, reaps it after 8 hours idle,
  and shrinks the cluster automatically. All 50 workspaces share one elastic pool.
- TRE cost grows with the **number of workspaces** (fixed); AKS cost grows with **actual
  concurrent usage** (elastic). Research usage is bursty and business-hours-bound — that idle
  gap is where the money goes in the TRE model.

## It holds up under pressure

| Scenario | AKS / mo | vs TRE |
|---|---|---|
| Modeled (40% peak, autoscaled) | $3,861 | **−71%** |
| Every session pinned on 24×7 (no scale-down) | $7,386 | **−45%** |
| With 1-yr Reserved Instances on baseline nodes | ~$2,500 | **~−81%** |

AKS only loses if utilization is ~100% **and** sustained 24/7/365 — which research never is.

## Not just cost — it's also better

- **More compute per active researcher:** 0.5 vCPU burst to 2 vCPU per session, vs ~0.4 vCPU
  shared on a P2v3 plan.
- **Per-user, per-app isolation** (one pod each) instead of one shared app per workspace.
- **Workspaces are free when idle** and cheap to create — no fixed per-workspace footprint.

## Assumptions (adjustable in `SRW_Cost_Model.xlsx`)
50 workspaces × 10 users · TRE P2v3 ($267/mo) · 40% peak concurrency · D8s_v3 nodes
($0.384/hr) · East US PAYG list prices. Cosmos DB and Service Bus excluded (already in prod
infra). Swap in your EA/CSP rate card — the structural conclusion does not change.

---
*Detail: `COST_ANALYSIS.md` · Interactive model: `SRW_Cost_Model.xlsx` (edit the yellow input cells).*
