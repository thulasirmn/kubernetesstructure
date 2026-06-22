using System;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Storage;
using Azure.ResourceManager.Storage.Models;
using Azure.Storage;
using Azure.Storage.Files.Shares;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SRW.Core.Abstractions;

namespace SRW.Infrastructure.Azure;

/// <summary>
/// Provisions and manages a Storage Account + File Share per workspace.
/// Uses Managed Identity in production (DefaultAzureCredential) — never store keys in config.
/// </summary>
public sealed class AzureStorageProvisioner : IAzureStorageProvisioner
{
    private readonly ArmClient _arm;
    private readonly AzureOptions _options;
    private readonly ILogger<AzureStorageProvisioner> _log;

    public AzureStorageProvisioner(
        IOptions<AzureOptions> options,
        ILogger<AzureStorageProvisioner> log)
    {
        _options = options.Value;
        _log = log;
        // DefaultAzureCredential picks up Managed Identity in AKS, az login locally,
        // env vars in CI — zero key storage required.
        _arm = new ArmClient(new DefaultAzureCredential());
    }

    public async Task<StorageProvisioningResult> ProvisionAsync(
        string resourceGroup,
        string storageAccountName,
        string fileShareName,
        long quotaInGiB,
        CancellationToken ct = default)
    {
        var subscription = await GetSubscriptionAsync(ct);
        var rg = await subscription.GetResourceGroupAsync(resourceGroup, ct);

        // 1) Storage account
        // For production lock-down, populate Azure:AllowedSubnetIds with the AKS subnet
        // and switch PublicNetworkAccess back to Disabled with private endpoints.
        var storageData = new StorageAccountCreateOrUpdateContent(
            new StorageSku(StorageSkuName.StandardLrs),
            StorageKind.StorageV2,
            new AzureLocation(_options.Region))
        {
            AccessTier = StorageAccountAccessTier.Hot,
            EnableHttpsTrafficOnly = true,
            MinimumTlsVersion = StorageMinimumTlsVersion.Tls1_2,
            PublicNetworkAccess = StoragePublicNetworkAccess.Enabled,
            AllowSharedKeyAccess = true // CSI driver uses the key (stored as a K8s Secret).
        };

        _log.LogInformation("Creating storage account {Account} in RG {RG}", storageAccountName, resourceGroup);
        var lro = await rg.Value.GetStorageAccounts()
            .CreateOrUpdateAsync(WaitUntil.Completed, storageAccountName, storageData, ct);
        var account = lro.Value;

        // 2) Fetch primary key — GetKeysAsync returns AsyncPageable<StorageAccountKey>
        string? primaryKey = null;
        await foreach (var k in account.GetKeysAsync(cancellationToken: ct))
        {
            primaryKey = k.Value;
            break;
        }
        if (primaryKey is null) throw new InvalidOperationException("Storage account returned no keys");

        // 3) File share
        var fileServices = account.GetFileService();
        var shareData = new FileShareData
        {
            ShareQuota = (int)quotaInGiB,
            EnabledProtocol = FileShareEnabledProtocol.Smb,
            AccessTier = FileShareAccessTier.TransactionOptimized
        };
        await fileServices.GetFileShares()
            .CreateOrUpdateAsync(WaitUntil.Completed, fileShareName, shareData, cancellationToken: ct);

        _log.LogInformation("File share {Share} created on {Account}", fileShareName, storageAccountName);

        return new StorageProvisioningResult(storageAccountName, fileShareName, primaryKey);
    }

    public async Task DeleteAsync(string resourceGroup, string storageAccountName, CancellationToken ct = default)
    {
        var subscription = await GetSubscriptionAsync(ct);
        var rg = await subscription.GetResourceGroupAsync(resourceGroup, ct);
        var account = await rg.Value.GetStorageAccountAsync(storageAccountName, cancellationToken: ct);
        await account.Value.DeleteAsync(WaitUntil.Completed, cancellationToken: ct);
    }

    public async Task<string> GetStorageKeyAsync(string resourceGroup, string storageAccountName, CancellationToken ct = default)
    {
        var subscription = await GetSubscriptionAsync(ct);
        var rg = await subscription.GetResourceGroupAsync(resourceGroup, ct);
        var account = await rg.Value.GetStorageAccountAsync(storageAccountName, cancellationToken: ct);

        await foreach (var k in account.Value.GetKeysAsync(cancellationToken: ct))
        {
            if (!string.IsNullOrEmpty(k.Value))
                return k.Value;
        }

        throw new InvalidOperationException($"Storage account '{storageAccountName}' returned no keys");
    }

    /// <summary>
    /// Returns the configured subscription if SubscriptionId is set, otherwise the credential's default.
    /// In corporate environments where users have access to many subscriptions, the "default" is rarely
    /// the one you want — always set Azure:SubscriptionId in appsettings for production.
    /// </summary>
    private async Task<SubscriptionResource> GetSubscriptionAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_options.SubscriptionId))
        {
            var resourceId = SubscriptionResource.CreateResourceIdentifier(_options.SubscriptionId);
            return _arm.GetSubscriptionResource(resourceId);
        }
        var defaultSub = await _arm.GetDefaultSubscriptionAsync(ct);
        return defaultSub;
    }

}

public class AzureOptions
{
    /// <summary>
    /// Optional. Explicit subscription ID for ARM operations. If empty, the credential's default
    /// subscription is used — usually NOT what you want when the user has multi-subscription access.
    /// Set this in appsettings to the subscription where workspaces should be created.
    /// </summary>
    public string? SubscriptionId { get; set; }

    public string Region { get; set; } = "eastus";
    public string AksClusterName { get; set; } = default!;
    public string AksResourceGroup { get; set; } = default!;
    public string IngressDomain { get; set; } = "research.example.com";
    public string[] AllowedSubnetIds { get; set; } = Array.Empty<string>();
}
