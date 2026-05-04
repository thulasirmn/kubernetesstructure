using Microsoft.Extensions.Options;
using SRW.Api.Auth;
using SRW.Core.Abstractions;
using SRW.Core.Services;
using SRW.Domain.Entities;
using SRW.Infrastructure.Messaging;

namespace SRW.Api.Endpoints;

public static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/workspaces").WithTags("Workspaces");

        // Returns 202 Accepted immediately; provisioning happens in WorkspaceProvisioningConsumer.
        grp.MapPost("/", async (
            CreateWorkspaceDto dto,
            IWorkspaceEnqueuer enqueuer,
            ICurrentUser user,
            CancellationToken ct) =>
        {
            var ws = await enqueuer.EnqueueAsync(
                new CreateWorkspaceRequest(dto.Name, dto.Description ?? "", dto.ResourceGroup, dto.QuotaGiB),
                user.UserId,
                user.DisplayName,
                ct);

            return Results.Accepted($"/api/workspaces/{ws.Id}", WorkspaceResponse.From(ws));
        });

        // List workspaces I'm a member of
        grp.MapGet("/", async (IWorkspaceRepository repo, ICurrentUser user, CancellationToken ct) =>
        {
            var list = await repo.ListForUserAsync(user.UserId, ct);
            return Results.Ok(list.Select(WorkspaceResponse.From));
        });

        // Get a single workspace
        grp.MapGet("/{id:guid}", async (Guid id, IWorkspaceRepository repo, CancellationToken ct) =>
        {
            var ws = await repo.GetByIdAsync(id, ct);
            return ws is null ? Results.NotFound() : Results.Ok(WorkspaceResponse.From(ws));
        });

        // Add a user to a workspace
        grp.MapPost("/{id:guid}/users", async (
            Guid id,
            AddUserDto dto,
            IWorkspaceRepository repo,
            CancellationToken ct) =>
        {
            var ws = await repo.GetByIdAsync(id, ct);
            if (ws is null) return Results.NotFound();

            ws.AddUser(dto.UserId, dto.DisplayName, Enum.Parse<WorkspaceRole>(dto.Role, true));
            await repo.UpdateAsync(ws, ct);
            return Results.NoContent();
        });

        // Initiate workspace deletion (async — sends cleanup message to Service Bus)
        grp.MapDelete("/{id:guid}", async (
            Guid id,
            IWorkspaceRepository repo,
            IServiceBusPublisher publisher,
            IOptions<ServiceBusOptions> sbOptions,
            CancellationToken ct) =>
        {
            var ws = await repo.GetByIdAsync(id, ct);
            if (ws is null) return Results.NotFound();

            ws.MarkDeleting();
            await repo.UpdateAsync(ws, ct);

            await publisher.PublishAsync(
                sbOptions.Value.WorkspaceCleanupQueue,
                new WorkspaceCleanupMessage(ws.Id),
                ct);

            return Results.Accepted();
        });
    }
}

public record CreateWorkspaceDto(string Name, string? Description, string ResourceGroup, long QuotaGiB = 100);
public record AddUserDto(string UserId, string DisplayName, string Role);

public record WorkspaceResponse(
    Guid Id,
    string Name,
    string Status,
    string StorageAccountName,
    string FileShareName,
    string K8sNamespace,
    DateTime CreatedAtUtc,
    int UserCount,
    IEnumerable<ApplicationSummary> Applications)
{
    public static WorkspaceResponse From(Workspace w) => new(
        w.Id, w.Name, w.Status.ToString(),
        w.StorageAccountName, w.FileShareName, w.K8sNamespace,
        w.CreatedAtUtc, w.Users.Count,
        w.Applications.Select(a => new ApplicationSummary(a.Id, a.Name, a.Type.ToString(), a.Enabled)));
}

public record ApplicationSummary(Guid Id, string Name, string Type, bool Enabled);
