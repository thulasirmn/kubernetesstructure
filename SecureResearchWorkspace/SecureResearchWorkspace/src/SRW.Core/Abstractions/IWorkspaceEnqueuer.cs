using SRW.Core.Services;
using SRW.Domain.Entities;

namespace SRW.Core.Abstractions;

/// <summary>
/// Creates the Workspace domain record and enqueues provisioning on Service Bus.
/// The caller gets the record back immediately (status = Pending) without waiting
/// for Azure Storage or Kubernetes provisioning.
/// </summary>
public interface IWorkspaceEnqueuer
{
    Task<Workspace> EnqueueAsync(CreateWorkspaceRequest request, string creatorUserId, string creatorDisplayName, CancellationToken ct = default);
}
