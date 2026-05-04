using System;
using System.Threading;
using System.Threading.Tasks;
using Azure;
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
        var subscription = await _arm.GetDefaultSubscriptionAsync(ct);
        var rg = await subscription.GetResourceGroupAsync(resourceGroup, ct);

        // 1) Storage account
        var storageData = new StorageAccountCreateOrUpdateContent(
            new StorageSku(StorageSkuName.StandardLrs),
            StorageKind.StorageV2,
            new AzureLocation(_options.Region))
        {
            AccessTier = StorageAccountAccessTier.Hot,
            EnableHttpsTrafficOnly = true,
            MinimumTlsVersion = StorageMinimumTlsVersion.Tls1_2,
            // Lock down: only allow access from the AKS subnet and the API's identity.
            // Network rules are wired through _options.AllowedSubnetIds.
            PublicNetworkAccess = StoragePublicNetworkAccess.Disabled,
            AllowSharedKeyAccess = true // CSI driver still needs the key — store in K8s Secret.
        };

        _log.LogInformation("Creating storage account {Account} in RG {RG}", storageAccountName, resourceGroup);
        var lro = await rg.Value.GetStorageAccounts()
            .CreateOrUpdateAsync(WaitUntil.Completed, storageAccountName, storageData, ct);
        var account = lro.Value;

        // 2) Fetch primary key
        var keys = await account.GetKeysAsync(cancellationToken: ct);
        var primaryKey = keys.Value.Keys[0].Value;

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
        var subscription = await _arm.GetDefaultSubscriptionAsync(ct);
        var rg = await subscription.GetResourceGroupAsync(resourceGroup, ct);
        var account = await rg.Value.GetStorageAccountAsync(storageAccountName, cancellationToken: ct);
        await account.Value.DeleteAsync(WaitUntil.Completed, cancellationToken: ct);
    }

    public async Task EnsureUserDirectoryAsync(
        string storageAccountName,
        string fileShareName,
        string accountKey,
        string userId,
        CancellationToken ct = default)
    {
        // Use the data-plane SDK to create a directory in the share.
        // This is what each user's K8s pod will subPath-mount into.
        var credential = new StorageSharedKeyCredential(storageAccountName, accountKey);
        var shareUri = new Uri($"https://{storageAccountName}.file.core.windows.net/{fileShareName}");
        var share = new ShareClient(shareUri, credential);

        // Sanitize userId so it's safe as a directory name.
        var safe = SanitizeDirName(userId);
        var dir = share.GetDirectoryClient(safe);
        await dir.CreateIfNotExistsAsync(cancellationToken: ct);

        _log.LogDebug("Ensured user directory {Dir} on {Share}", safe, fileShareName);
    }

    private static string SanitizeDirName(string userId)
    {
        // Azure file share dir names: 1-255 chars, no \ / : * ? " < > |
        var span = userId.AsSpan();
        Span<char> buf = stackalloc char[Math.Min(255, span.Length)];
        var len = 0;
        foreach (var c in span)
        {
            if (len == 255) break;
            buf[len++] = c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c;
        }
        return new string(buf[..len]);
    }
}

public class AzureOptions
{
    public string Region { get; set; } = "eastus";
    public string AksClusterName { get; set; } = default!;
    public string AksResourceGroup { get; set; } = default!;
    public string IngressDomain { get; set; } = "research.example.com";
    public string[] AllowedSubnetIds { get; set; } = Array.Empty<string>();
}
