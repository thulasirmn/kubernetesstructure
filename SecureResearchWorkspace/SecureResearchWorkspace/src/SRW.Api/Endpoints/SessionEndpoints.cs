using Microsoft.Extensions.Options;
using SRW.Api.Auth;
using SRW.Core.Abstractions;
using SRW.Core.Services;
using SRW.Domain.Entities;
using SRW.Infrastructure.Messaging;

namespace SRW.Api.Endpoints;

public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/workspaces/{workspaceId:guid}/sessions").WithTags("Sessions");

        // Launch a session (idempotent — returns existing if already running)
        grp.MapPost("/", async (
            Guid workspaceId,
            LaunchSessionDto dto,
            SessionLauncher launcher,
            ICurrentUser user,
            CancellationToken ct) =>
        {
            try
            {
                var session = await launcher.LaunchAsync(workspaceId, dto.ApplicationId, user.UserId, ct);
                return Results.Ok(SessionResponse.From(session));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // List my sessions in this workspace
        grp.MapGet("/", async (
            Guid workspaceId,
            ISessionRepository repo,
            ICurrentUser user,
            CancellationToken ct) =>
        {
            var sessions = await repo.ListForUserAsync(workspaceId, user.UserId, ct);
            return Results.Ok(sessions.Select(SessionResponse.From));
        });

        // Get one session
        grp.MapGet("/{sessionId:guid}", async (
            Guid workspaceId,
            Guid sessionId,
            ISessionRepository repo,
            CancellationToken ct) =>
        {
            var s = await repo.GetByIdAsync(sessionId, ct);
            if (s is null || s.WorkspaceId != workspaceId) return Results.NotFound();
            return Results.Ok(SessionResponse.From(s));
        });

        // Stop a session — returns 202 immediately; SessionStopConsumer does the K8s teardown.
        grp.MapDelete("/{sessionId:guid}", async (
            Guid workspaceId,
            Guid sessionId,
            ISessionRepository repo,
            IServiceBusPublisher publisher,
            IOptions<ServiceBusOptions> sbOptions,
            CancellationToken ct) =>
        {
            var session = await repo.GetByIdAsync(sessionId, ct);
            if (session is null || session.WorkspaceId != workspaceId)
                return Results.NotFound();

            if (session.Status == SessionStatus.Stopped)
                return Results.NoContent();

            session.Status = SessionStatus.Stopping;
            await repo.UpdateAsync(session, ct);

            await publisher.PublishAsync(
                sbOptions.Value.SessionStopQueue,
                new SessionStopMessage(session.Id, session.WorkspaceId),
                ct);

            return Results.Accepted();
        });
    }
}

public record LaunchSessionDto(Guid ApplicationId);

public record SessionResponse(
    Guid Id,
    Guid WorkspaceId,
    Guid ApplicationId,
    string Status,
    string? AccessUrl,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc)
{
    public static SessionResponse From(UserSession s) => new(
        s.Id, s.WorkspaceId, s.ApplicationId,
        s.Status.ToString(), s.AccessUrl,
        s.CreatedAtUtc, s.StartedAtUtc);
}
