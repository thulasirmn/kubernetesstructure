# Cost Analysis: AKS (SRW) vs TRE App Service per Workspace

**Question:** What is the cost advantage of the SRW AKS implementation over the current
Azure TRE model, where **each workspace provisions its own App Service Plan** to host
Jupyter, RStudio, DBGate and FDSA?

**Modeled scenario (agreed inputs):**

| Input | Value |
|---|---|
| Workspaces | 50 |
| Users per workspace | 10 (**500 total**) |
| Apps per workspace | 4 — Jupyter, RStudio, DBGate, FDSA |
| TRE App Service Plan SKU | **P2v3** (4 vCPU, 16 GB) — one plan per workspace |
| Peak concurrency | **~40%** = 200 concurrent sessions |
| Region / pricing basis | East US, PAYG list prices, 730 hrs/mo, USD |

> Prices are representative PAYG list rates (validated June 2026 against the Azure pricing
> pages — `D8s_v3` = $0.384/hr; App Service P2v3 Linux ≈ $0.366/hr). Apply your EA/CSP
> discount uniformly to both sides — it does **not** change the structural conclusion.

---

## The structural difference (why AKS wins)

| | TRE — App Service per workspace | SRW — shared AKS cluster |
|---|---|---|
| Unit of cost | **1 App Service Plan per workspace** | **1 shared cluster + pods on demand** |
| Billing when idle | **24×7×365** — App Service Plans cannot scale to zero; you pay even with nobody logged in | **Near-zero** — session pods are created on launch, **reaped after 8h idle**; autoscaler removes empty nodes |
| Scales with | **Number of workspaces** (fixed) | **Actual concurrent sessions** (elastic) |
| Bin-packing | None — each plan is dedicated to one workspace | All workspaces share one elastic node pool |
| Compute per active user | 4 vCPU ÷ 4 apps ÷ 10 users ≈ **0.4 vCPU** worst case | **0.5 vCPU request, burst to 2 vCPU**, 1–4 GiB per session |

The core insight: **TRE pays for 50 plans running continuously regardless of use; AKS pays
only for sessions that are actually running.** Research usage is bursty and concentrated in
business hours, so the idle gap is where the savings live.

---

## Model A — TRE App Service (current)

50 workspaces × 1 × P2v3 plan, running 24/7:

```
50 plans × $267.18/mo (P2v3 Linux)  =  $13,359 / mo  ≈  $160,300 / yr
```

This is a **fixed floor**. It does not drop when researchers go home, and it rises to
**$26,700/mo** if any workspaces need P3v3 for heavier compute. Storage (file shares) is
excluded — it is identical on both sides.

---

## Model B — SRW on AKS

### Fixed platform cost

| Component | Spec | $/mo |
|---|---|---|
| AKS control plane (Standard, uptime SLA) | $0.10/hr | 73 |
| System node pool | 2 × D4s_v3 ($140/mo) — API, ingress-nginx, coredns | 280 |
| Standard Load Balancer + public IP | ingress entrypoint | 25 |
| **Fixed subtotal** | | **~378** |

> Cosmos DB and Service Bus are **excluded** — these already exist in the production
> infrastructure and are shared, so they add no incremental cost to this deployment.

### Variable cost — user session pods (the elastic part)

Node sizing uses the **conservative ratio from `RESOURCE_TOPOLOGY.md`** (~8 sessions per
`D8s_v3` node; the real number is higher by CPU request, so this *understates* AKS efficiency).

- Peak: 200 concurrent → **25 × D8s_v3**, ~10 business hrs/day × 22 days = **220 hrs/mo**
- Off-peak baseline: ~50 long-running sessions → **7 nodes** for the remaining **510 hrs/mo**

```
Node-hours = (25 × 220) + (7 × 510) = 5,500 + 3,570 = 9,070 node-hrs/mo
Compute    = 9,070 × $0.384/hr      = $3,483 / mo
```

### AKS total

```
$3,483 (sessions) + $378 (fixed)  =  $3,861 / mo  ≈  $46,330 / yr
```

---

## Result

| | Per month | Per year |
|---|---|---|
| TRE — 50 × P2v3 App Service Plans | **$13,359** | **$160,300** |
| SRW — shared AKS | **$3,861** | **$46,330** |
| **Savings** | **$9,498** | **~$114,000** |
| **% reduction** | | **~71%** |

---

## Robustness — even the worst case favors AKS

| Scenario | AKS $/mo | vs TRE $13,359 |
|---|---|---|
| Modeled (40% peak, autoscaled) | $3,861 | **−71%** |
| **Always-on** — 200 sessions pinned 24/7 (25 nodes × 730h) | $7,386 | **−45%** |
| Pathological — every workspace at P3v3 in TRE | $3,861 vs $26,700 | **−86%** |

Even if AKS never scaled down at all, the shared/bin-packed cluster still beats 50 dedicated
plans. AKS only loses if utilization is ~100% **and** sustained 24/7/365 — which research
workloads never are.

### Further AKS savings (not in the headline number)
- **1-yr Reserved Instances / Savings Plan** on the baseline node pool: ~40% off → AKS ≈ **$2,500/mo (~81% reduction)**.
- **Spot node pool** for batch/non-interactive sessions: D8s_v3 spot ≈ $0.074/hr (~80% off). Use with caution for interactive sessions (eviction).
- **Right-sizing requests** below the conservative 8-per-node ratio increases density further.

---

## Beyond cost — what AKS also buys

- **More compute per active researcher** (0.5→2 vCPU burst vs ~0.4 vCPU shared on a P2v3).
- **Per-user, per-app isolation** (one pod each) vs one shared app instance per workspace.
- **Namespace + NetworkPolicy isolation** between workspaces on one cluster.
- **No per-workspace provisioning of fixed infrastructure** — workspaces are cheap to create and free when idle.

---

## Caveats / how to refine

1. Replace P2v3 and D8s_v3 rates with your **EA/CSP rate card** (apply to both sides).
2. The 8-sessions/node ratio is deliberately conservative; measure real pod density to tighten.
3. Concurrency profile (220 peak hrs/mo, 50-session off-peak baseline) is the biggest lever —
   adjust to your observed telemetry.
4. Shared TRE-core services (firewall, app gateway, etc.) and per-workspace storage are
   excluded as roughly comparable / out of scope for this compute delta.
```

