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

        // Seed default application catalog.
        workspace.Applications.Add(new WorkspaceApplication
        {
            Id             = Guid.NewGuid(),
            WorkspaceId    = workspace.Id,
            Name           = "Jupyter Notebook",
            Type           = ApplicationType.Jupyter,
            ContainerImage = "jupyter/scipy-notebook:latest",
            ContainerPort  = 8888,
            MountPath      = "/home/jovyan/work",
            CommandJson    = """["start-notebook.sh","--NotebookApp.token=''","--NotebookApp.password=''","--NotebookApp.base_url=__BASE_URL__"]"""
        });
        workspace.Applications.Add(new WorkspaceApplication
        {
            Id              = Guid.NewGuid(),
            WorkspaceId     = workspace.Id,
            Name            = "RStudio Server",
            Type            = ApplicationType.RStudio,
            ContainerImage  = "rocker/rstudio:latest",
            ContainerPort   = 8787,
            MountPath       = "/home/rstudio/work",
            EnvironmentJson = """{"DISABLE_AUTH":"true","ROOT":"true"}"""
        });

        workspace.AddUser(creatorUserId, creatorDisplayName, WorkspaceRole.Admin);

        await _repo.AddAsync(workspace, ct);

        await _bus.PublishAsync(
            _options.WorkspaceProvisionQueue,
            new WorkspaceProvisionMessage(workspace.Id),
            ct);

        return workspace;
    }
}
