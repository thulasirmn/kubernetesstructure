using System.Threading;
using System.Threading.Tasks;

namespace SRW.Core.Abstractions;

/// <summary>
/// Provisions and manages the Azure Storage Account + File Share that backs a workspace.
/// </summary>
public interface IAzureStorageProvisioner
{
    /// <summary>
    /// Creates a new Storage Account in the given resource group, then creates a single File Share
    /// inside it. Returns the storage account access key (caller stores this in K8s as a Secret
    /// for the CSI driver to consume).
    /// </summary>
    Task<StorageProvisioningResult> ProvisionAsync(
        string resourceGroup,
        string storageAccountName,
        string fileShareName,
        long quotaInGiB,
        CancellationToken ct = default);

    Task DeleteAsync(string resourceGroup, string storageAccountName, CancellationToken ct = default);
}

public record StorageProvisioningResult(
    string StorageAccountName,
    string FileShareName,
    string AccountKey);
