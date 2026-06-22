using SRW.Core.Abstractions;
using SRW.Domain.Entities;

namespace SRW.Api.Endpoints;

public static class ApplicationEndpoints
{
    public static void MapApplicationEndpoints(this WebApplication app)
    {
        // ── Global catalog (/api/applications) ───────────────────────────────

        var catalog = app.MapGroup("/api/applications").WithTags("Applications");

        // List catalog (enabled only by default; ?includeDisabled=true for admins)
        catalog.MapGet("/", async (
            bool? includeDisabled,
            IApplicationRepository repo,
            CancellationToken ct) =>
        {
            var apps = await repo.ListAsync(includeDisabled ?? false, ct);
            return Results.Ok(apps.Select(ApplicationResponse.From));
        });

        // Get one catalog entry
        catalog.MapGet("/{id:guid}", async (
            Guid id,
            IApplicationRepository repo,
            CancellationToken ct) =>
        {
            var app = await repo.GetByIdAsync(id, ct);
            return app is null ? Results.NotFound() : Results.Ok(ApplicationResponse.From(app));
        });

        // Create application in catalog (workspace admin supplies ACR image)
        catalog.MapPost("/", async (
            CreateApplicationDto dto,
            IApplicationRepository repo,
            CancellationToken ct) =>
        {
            var application = new WorkspaceApplication
            {
                Id              = Guid.NewGuid(),
                Name            = dto.Name,
                Type            = Enum.Parse<ApplicationType>(dto.Type, ignoreCase: true),
                ContainerImage  = dto.ContainerImage,
                ContainerPort   = dto.ContainerPort,
                MountPath       = dto.MountPath       ?? "/home/jovyan/work",
                CpuRequest      = dto.CpuRequest      ?? "100m",
                CpuLimit        = dto.CpuLimit        ?? "2",
                MemoryRequest   = dto.MemoryRequest   ?? "1Gi",
                MemoryLimit     = dto.MemoryLimit     ?? "4Gi",
                EnvironmentJson = dto.EnvironmentJson ?? "{}",
                CommandJson     = dto.CommandJson,
                Enabled         = true,
                CreatedAtUtc    = DateTime.UtcNow
            };
            await repo.AddAsync(application, ct);
            return Results.Created($"/api/applications/{application.Id}", ApplicationResponse.From(application));
        });

        // Update catalog entry (image, resources, etc.)
        catalog.MapPut("/{id:guid}", async (
            Guid id,
            UpdateApplicationDto dto,
            IApplicationRepository repo,
            CancellationToken ct) =>
        {
            var existing = await repo.GetByIdAsync(id, ct);
            if (existing is null) return Results.NotFound();

            existing.Name            = dto.Name            ?? existing.Name;
            existing.ContainerImage  = dto.ContainerImage  ?? existing.ContainerImage;
            existing.ContainerPort   = dto.ContainerPort   ?? existing.ContainerPort;
            existing.MountPath       = dto.MountPath       ?? existing.MountPath;
            existing.CpuRequest      = dto.CpuRequest      ?? existing.CpuRequest;
            existing.CpuLimit        = dto.CpuLimit        ?? existing.CpuLimit;
            existing.MemoryRequest   = dto.MemoryRequest   ?? existing.MemoryRequest;
            existing.MemoryLimit     = dto.MemoryLimit     ?? existing.MemoryLimit;
            existing.EnvironmentJson = dto.EnvironmentJson ?? existing.EnvironmentJson;
            existing.CommandJson     = dto.CommandJson     ?? existing.CommandJson;

            await repo.UpdateAsync(existing, ct);
            return Results.Ok(ApplicationResponse.From(existing));
        });

        // Soft-delete (Enabled = false). Existing running sessions are unaffected.
        catalog.MapDelete("/{id:guid}", async (
            Guid id,
            IApplicationRepository repo,
            CancellationToken ct) =>
        {
            var existing = await repo.GetByIdAsync(id, ct);
            if (existing is null) return Results.NotFound();
            await repo.DisableAsync(id, ct);
            return Results.NoContent();
        });

        // Assign this application to one or more workspaces
        catalog.MapPost("/{id:guid}/assign", async (
            Guid id,
            AssignApplicationDto dto,
            IApplicationRepository appRepo,
            IWorkspaceRepository wsRepo,
            CancellationToken ct) =>
        {
            var application = await appRepo.GetByIdAsync(id, ct);
            if (application is null) return Results.NotFound(new { error = "Application not found." });
            if (!application.Enabled) return Results.BadRequest(new { error = "Cannot assign a disabled application." });

            var assigned = new List<Guid>();
            var notFound = new List<Guid>();

            foreach (var wsId in dto.WorkspaceIds.Distinct())
            {
                var ws = await wsRepo.GetByIdAsync(wsId, ct);
                if (ws is null) { notFound.Add(wsId); continue; }
                await wsRepo.AssignApplicationAsync(wsId, id, ct);
                assigned.Add(wsId);
            }

            return Results.Ok(new { assigned, notFound });
        });

        // ── Workspace-scoped views (/api/workspaces/{id}/applications) ────────

        var wsApps = app.MapGroup("/api/workspaces/{workspaceId:guid}/applications").WithTags("Applications");

        // List apps assigned to this workspace (fetches full details from catalog)
        wsApps.MapGet("/", async (
            Guid workspaceId,
            IWorkspaceRepository wsRepo,
            IApplicationRepository appRepo,
            CancellationToken ct) =>
        {
            var ws = await wsRepo.GetByIdAsync(workspaceId, ct);
            if (ws is null) return Results.NotFound();

            var apps = await appRepo.GetByIdsAsync(ws.ApplicationIds, ct);
            return Results.Ok(apps.Where(a => a.Enabled).Select(ApplicationResponse.From));
        });

        // Unassign an application from this workspace
        wsApps.MapDelete("/{appId:guid}", async (
            Guid workspaceId,
            Guid appId,
            IWorkspaceRepository wsRepo,
            CancellationToken ct) =>
        {
            var ws = await wsRepo.GetByIdAsync(workspaceId, ct);
            if (ws is null) return Results.NotFound();
            await wsRepo.UnassignApplicationAsync(workspaceId, appId, ct);
            return Results.NoContent();
        });
    }
}

public record CreateApplicationDto(
    string Name,
    string Type,
    string ContainerImage,
    int ContainerPort,
    string? MountPath,
    string? CpuRequest,
    string? CpuLimit,
    string? MemoryRequest,
    string? MemoryLimit,
    string? EnvironmentJson,
    string? CommandJson);

public record UpdateApplicationDto(
    string? Name,
    string? ContainerImage,
    int? ContainerPort,
    string? MountPath,
    string? CpuRequest,
    string? CpuLimit,
    string? MemoryRequest,
    string? MemoryLimit,
    string? EnvironmentJson,
    string? CommandJson);

public record AssignApplicationDto(List<Guid> WorkspaceIds);

public record ApplicationResponse(
    Guid Id,
    string Name,
    string Type,
    string ContainerImage,
    int ContainerPort,
    string CpuRequest,
    string MemoryRequest,
    string MountPath,
    bool Enabled,
    DateTime CreatedAtUtc)
{
    public static ApplicationResponse From(WorkspaceApplication a) => new(
        a.Id, a.Name, a.Type.ToString(), a.ContainerImage,
        a.ContainerPort, a.CpuRequest, a.MemoryRequest,
        a.MountPath, a.Enabled, a.CreatedAtUtc);
}
