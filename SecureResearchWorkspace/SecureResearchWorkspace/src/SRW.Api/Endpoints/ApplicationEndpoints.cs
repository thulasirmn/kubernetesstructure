using SRW.Core.Abstractions;
using SRW.Domain.Entities;

namespace SRW.Api.Endpoints;

public static class ApplicationEndpoints
{
    public static void MapApplicationEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/workspaces/{workspaceId:guid}/applications").WithTags("Applications");

        // List apps available in this workspace
        grp.MapGet("/", async (Guid workspaceId, IWorkspaceRepository repo, CancellationToken ct) =>
        {
            var apps = await repo.GetApplicationsAsync(workspaceId, ct);
            return Results.Ok(apps.Select(a => new
            {
                a.Id, a.Name, Type = a.Type.ToString(), a.ContainerImage, a.MountPath, a.Enabled
            }));
        });

        // Register a custom application (custom Docker image)
        grp.MapPost("/", async (
            Guid workspaceId,
            RegisterAppDto dto,
            IWorkspaceRepository repo,
            CancellationToken ct) =>
        {
            var ws = await repo.GetByIdAsync(workspaceId, ct);
            if (ws is null) return Results.NotFound();

            var application = new WorkspaceApplication
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                Name = dto.Name,
                Type = ApplicationType.Custom,
                ContainerImage = dto.ContainerImage,
                ContainerPort = dto.ContainerPort,
                MountPath = dto.MountPath ?? "/workspace",
                CpuRequest = dto.CpuRequest ?? "500m",
                CpuLimit = dto.CpuLimit ?? "2",
                MemoryRequest = dto.MemoryRequest ?? "1Gi",
                MemoryLimit = dto.MemoryLimit ?? "4Gi",
                EnvironmentJson = dto.EnvironmentJson ?? "{}",
                CommandJson = dto.CommandJson
            };
            ws.Applications.Add(application);
            await repo.UpdateAsync(ws, ct);

            return Results.Created($"/api/workspaces/{workspaceId}/applications/{application.Id}", new { application.Id });
        });
    }
}

public record RegisterAppDto(
    string Name,
    string ContainerImage,
    int ContainerPort,
    string? MountPath,
    string? CpuRequest,
    string? CpuLimit,
    string? MemoryRequest,
    string? MemoryLimit,
    string? EnvironmentJson,
    string? CommandJson);
