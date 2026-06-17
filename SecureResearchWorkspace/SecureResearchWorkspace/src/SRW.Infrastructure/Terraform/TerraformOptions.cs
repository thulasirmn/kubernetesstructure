namespace SRW.Infrastructure.Terraform;

public sealed class TerraformOptions
{
    public string TerraformBinaryPath { get; set; } = "terraform";

    /// <summary>Absolute path to the directory containing the terraform/modules/* subdirectories.</summary>
    public string ModulesBasePath { get; set; } = "terraform/modules";

    /// <summary>Root directory where per-workspace/session working directories are created.</summary>
    public string WorkingRootDir { get; set; } = "/tmp/srw-terraform";

    /// <summary>
    /// Shared Terraform plugin cache. Persisting this on a PVC avoids re-downloading providers
    /// on every pod restart.
    /// </summary>
    public string PluginCacheDir { get; set; } = "/tmp/terraform-plugins";

    // ── Remote state backend (Azure Blob) ────────────────────────────────────

    /// <summary>Storage account that holds Terraform state blobs.</summary>
    public string StateStorageAccount { get; set; } = "";

    /// <summary>Resource group of the state storage account.</summary>
    public string StateResourceGroup { get; set; } = "";

    /// <summary>Blob container name within the state storage account.</summary>
    public string StateContainer { get; set; } = "srw-tf-state";

    // ── Azure identity ────────────────────────────────────────────────────────

    /// <summary>Azure subscription ID for the azurerm provider.</summary>
    public string SubscriptionId { get; set; } = "";

    /// <summary>Tenant ID for Workload Identity OIDC auth (AKS only).</summary>
    public string TenantId { get; set; } = "";

    /// <summary>Client ID of the managed identity for Workload Identity (AKS only).</summary>
    public string ClientId { get; set; } = "";

    // ── Session / workspace module config ────────────────────────────────────

    public string Region { get; set; } = "eastus";

    public string IngressDomain { get; set; } = "research.example.com";
}
