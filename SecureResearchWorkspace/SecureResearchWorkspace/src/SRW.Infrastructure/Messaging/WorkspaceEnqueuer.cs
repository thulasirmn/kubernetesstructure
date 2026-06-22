using Microsoft.Extensions.Options;
using SRW.Core.Abstractions;
using SRW.Core.Services;
using SRW.Domain.Entities;

namespace SRW.Infrastructure.Messaging;

/// <summary>
/// Implements the non-blocking workspace creation path:
///   1. Persist the Workspace record with status = Pending.
///   2. Publish a WorkspaceProvisionMessage to Service Bus.
/// The caller receives the Workspace immediately; actual Azure provisioning
/// happens in WorkspaceProvisioningConsumer (BackgroundService).
/// </summary>
public sealed class WorkspaceEnqueuer : IWorkspaceEnqueuer
{
    private readonly IWorkspaceRepository _repo;
    private readonly IServiceBusPublisher _bus;
    private readonly ServiceBusOptions    _options;

    public WorkspaceEnqueuer(
        IWorkspaceRepository repo,
        IServiceBusPublisher bus,
        IOptions<ServiceBusOptions> options)
    {
        _repo    = repo;
        _bus     = bus;
        _options = options.Value;
    }

    public async Task<Workspace> EnqueueAsync(
        CreateWorkspaceRequest request,
        string creatorUserId,
        string creatorDisplayName,
        CancellationToken ct = default)
    {
        var workspace = Workspace.Create(
            request.Name,
            request.Description,
            request.ResourceGroup,
            request.QuotaInGiB);

        // Applications are assigned from the global catalog via POST /api/applications/{id}/assign.
        // New workspaces start with an empty ApplicationIds list.

        workspace.AddUser(creatorUserId, creatorDisplayName, WorkspaceRole.Admin);

        await _repo.AddAsync(workspace, ct);

        await _bus.PublishAsync(
            _options.WorkspaceProvisionQueue,
            new WorkspaceProvisionMessage(workspace.Id),
            ct);

        return workspace;
    }
}
