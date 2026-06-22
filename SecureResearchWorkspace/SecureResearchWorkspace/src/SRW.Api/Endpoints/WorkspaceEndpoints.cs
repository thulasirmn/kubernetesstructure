using Microsoft.Extensions.Options;
using SRW.Api.Auth;
using SRW.Core.Abstractions;
using SRW.Core.Services;
using SRW.Domain.Entities;
using SRW.Infrastructure.Azure;
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

        // ── Blob mount endpoints ──────────────────────────────────────────────────

        // Add a read-only blob container mount to the workspace.
        // Fetches the storage key from ARM — caller never passes a key.
        // All currently Running sessions are restarted to pick up the new mount.
        grp.MapPost("/{id:guid}/blob-mounts", async (
            Guid id,
            AddBlobMountDto dto,
            IWorkspaceRepository workspaceRepo,
            ISessionRepository sessionRepo,
            IApplicationRepository appRepo,
            IAzureStorageProvisioner storage,
            IKubernetesOrchestrator k8s,
            CancellationToken ct) =>
        {
            var ws = await workspaceRepo.GetByIdAsync(id, ct);
            if (ws is null) return Results.NotFound();

            var mountPath = string.IsNullOrWhiteSpace(dto.MountPath)
                ? $"/mnt/blobs/{dto.ContainerName}"
                : dto.MountPath;

            var entry = ws.AddBlobMount(dto.StorageAccountName, dto.ResourceGroup, dto.ContainerName, mountPath);

            // Fetch storage key from ARM, create K8s Secret, then create PV+PVC.
            // PVC uses mountOptions (uid=1000,gid=100,allow_other) which are not permitted
            // on inline ephemeral CSI volumes but ARE allowed on PersistentVolumes.
            var secretName = $"blob-creds-{entry.Id:N}";
            var key = await storage.GetStorageKeyAsync(dto.ResourceGroup, dto.StorageAccountName, ct);
            await k8s.EnsureBlobCredentialSecretAsync(ws.K8sNamespace, secretName, dto.StorageAccountName, key, ct);
            await k8s.EnsureBlobPvcAsync(ws.K8sNamespace, entry.Id.ToString("N"), dto.StorageAccountName, dto.ContainerName, secretName, ct);

            await workspaceRepo.UpdateAsync(ws, ct);

            // Restart all Running sessions so the new volume is mounted immediately.
            var runningSessions = await sessionRepo.ListRunningByWorkspaceAsync(id, ct);
            foreach (var session in runningSessions)
            {
                var app = await appRepo.GetByIdAsync(session.ApplicationId, ct);
                if (app is not null)
                    await k8s.LaunchSessionAsync(ws, app, session, ct);
            }

            return Results.Created($"/api/workspaces/{id}/blob-mounts/{entry.Id}", BlobMountResponse.From(entry));
        });

        // List all blob mounts for a workspace.
        grp.MapGet("/{id:guid}/blob-mounts", async (
            Guid id,
            IWorkspaceRepository repo,
            CancellationToken ct) =>
        {
            var ws = await repo.GetByIdAsync(id, ct);
            return ws is null
                ? Results.NotFound()
                : Results.Ok(ws.BlobMounts.Select(BlobMountResponse.From));
        });

        // Remove a blob mount. Deletes its K8s Secret and restarts Running sessions.
        grp.MapDelete("/{id:guid}/blob-mounts/{mountId:guid}", async (
            Guid id,
            Guid mountId,
            IWorkspaceRepository workspaceRepo,
            ISessionRepository sessionRepo,
            IApplicationRepository appRepo,
            IKubernetesOrchestrator k8s,
            CancellationToken ct) =>
        {
            var ws = await workspaceRepo.GetByIdAsync(id, ct);
            if (ws is null) return Results.NotFound();
            if (!ws.RemoveBlobMount(mountId)) return Results.NotFound();

            await k8s.DeleteBlobPvcAsync(ws.K8sNamespace, mountId.ToString("N"), ct);
            await k8s.DeleteBlobCredentialSecretAsync(ws.K8sNamespace, $"blob-creds-{mountId:N}", ct);
            await workspaceRepo.UpdateAsync(ws, ct);

            var runningSessions = await sessionRepo.ListRunningByWorkspaceAsync(id, ct);
            foreach (var session in runningSessions)
            {
                var app = await appRepo.GetByIdAsync(session.ApplicationId, ct);
                if (app is not null)
                    await k8s.LaunchSessionAsync(ws, app, session, ct);
            }

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
public record AddBlobMountDto(string StorageAccountName, string ResourceGroup, string ContainerName, string? MountPath);

public record BlobMountResponse(Guid Id, string StorageAccountName, string ResourceGroup, string ContainerName, string MountPath, DateTime RequestedAtUtc)
{
    public static BlobMountResponse From(BlobMountEntry m) =>
        new(m.Id, m.StorageAccountName, m.ResourceGroup, m.ContainerName, m.MountPath, m.RequestedAtUtc);
}

public record WorkspaceResponse(
    Guid Id,
    string Name,
    string Status,
    string StorageAccountName,
    string FileShareName,
    string K8sNamespace,
    DateTime CreatedAtUtc,
    int UserCount,
    IEnumerable<Guid> ApplicationIds)
{
    public static WorkspaceResponse From(Workspace w) => new(
        w.Id, w.Name, w.Status.ToString(),
        w.StorageAccountName, w.FileShareName, w.K8sNamespace,
        w.CreatedAtUtc, w.Users.Count,
        w.ApplicationIds);
}
