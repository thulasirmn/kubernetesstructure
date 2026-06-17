import os

# Use the portable Graphviz we extracted (no admin install needed).
GV_BIN = os.path.join(os.environ["LOCALAPPDATA"], "graphviz-portable", "Graphviz-12.2.1-win64", "bin")
os.environ["PATH"] = GV_BIN + os.pathsep + os.environ.get("PATH", "")

from diagrams import Diagram, Cluster, Edge
from diagrams.onprem.client import Users
from diagrams.azure.database import CosmosDb
from diagrams.azure.integration import ServiceBus
from diagrams.azure.storage import AzureFileshares
from diagrams.k8s.compute import Deploy, Pod
from diagrams.k8s.network import Service, Ing, NetworkPolicy
from diagrams.k8s.podconfig import Secret
from diagrams.k8s.rbac import SA

OUT = r"D:\kubernetesstructure\SecureResearchWorkspace\SecureResearchWorkspace\docs\architecture_azure"

graph_attr = {
    "fontsize": "24",
    "bgcolor": "white",
    "splines": "ortho",      # right-angle connectors -> aligned, tidy routing
    "nodesep": "0.9",
    "ranksep": "1.6",
    "pad": "0.6",
    "concentrate": "false",
}
node_attr = {"fontsize": "13", "margin": "0.1"}
edge_attr = {"fontsize": "12"}

# secondary edges: don't let them distort the main left-to-right ranking
SEC = {"constraint": "false", "color": "gray45"}
GREEN = {"constraint": "false", "color": "darkgreen", "penwidth": "2.2"}
PROV = {"constraint": "false", "color": "#7d3c98", "style": "dotted"}

with Diagram(
    "Secure Research Workspace on AKS  -  Architecture & App-Launch Flow",
    filename=OUT,
    outformat=["png", "svg"],
    direction="LR",
    show=False,
    graph_attr=graph_attr,
    node_attr=node_attr,
    edge_attr=edge_attr,
):
    user = Users("Researcher\n(X-User-Id = email)")

    with Cluster("Azure Subscription   |   RG: srw-platform-rg"):

        with Cluster("AKS Cluster (srw-aks)  -  1 shared, managed control plane"):

            ingress = Ing("ingress-nginx\n(/s/<slug>/ , /api)")

            with Cluster("namespace: srw-platform"):
                sa = SA("srw-api SA\n+ ClusterRole RBAC")
                api = Deploy("SRW.Api\n(2 replicas)")
                workers = Pod("Background workers\nProvision / Stop / Poller\nIdleReaper / OrphanCleaner")

            with Cluster("namespace: ws-<name>-<id>  (one per workspace)"):
                dep = Deploy("Deployment sess-<id>\nJupyter / RStudio / DBGate / FDSA\nreq 0.5-2 vCPU / 1-4 GiB")
                svc = Service("svc-<id>\n(ClusterIP)")
                win = Ing("Ingress rule\n/s/<slug>/")
                sec = Secret("azure-storage-creds")
                npol = NetworkPolicy("default-deny")

        cosmos = CosmosDb("Cosmos DB (srw)\nworkspaces | sessions | secrets")
        sbus = ServiceBus("Service Bus\nprovision | stop | cleanup")
        storage = AzureFileshares("Storage Account srw<guid>\nFile Share: workspace-share (SMB)\n1 per workspace")

    # ---- Main launch-flow spine (solid, numbered, constraining) ----
    user >> Edge(label="1  POST /workspaces/{id}/sessions") >> ingress
    ingress >> Edge(label="2") >> api
    api >> Edge(label="3  idempotency + session doc") >> cosmos
    api >> Edge(label="4  create Deployment + Service + Ingress") >> dep
    dep >> Edge(label="5  SMB mount (file.csi.azure.com)") >> storage

    # ---- Secondary relationships (dashed, non-constraining) ----
    sa >> Edge(label="authorizes", style="dashed", **SEC) >> api
    workers >> Edge(label="6  poll ready -> Running", style="dashed", **SEC) >> dep
    api >> Edge(label="7  return AccessUrl", style="dashed", **SEC) >> user
    dep >> Edge(label="reads", style="dashed", **SEC) >> sec
    npol >> Edge(label="isolates", style="dashed", **SEC) >> dep
    win >> Edge(label="registers route", style="dashed", **SEC) >> ingress

    # ---- Runtime traffic (thick green) ----
    user >> Edge(label="8  open app URL", **GREEN) >> ingress
    ingress >> Edge(**GREEN) >> svc >> Edge(**GREEN) >> dep

    # ---- Async provisioning (dotted purple) ----
    api >> Edge(label="create workspace -> enqueue", **PROV) >> sbus
    sbus >> Edge(label="consumed by", **PROV) >> workers
    workers >> Edge(label="provision ns + secret + netpol", **PROV) >> storage

print("saved", OUT + ".png", "and", OUT + ".svg")
