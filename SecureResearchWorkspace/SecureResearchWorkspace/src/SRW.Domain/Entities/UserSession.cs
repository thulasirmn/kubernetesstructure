using System;

namespace SRW.Domain.Entities;

/// <summary>
/// A UserSession represents a *running* instance of a WorkspaceApplication for a specific user.
/// One user can have multiple concurrent sessions (e.g. Jupyter AND RStudio at once).
/// Maps to: 1 K8s Deployment + 1 Service + 1 Ingress rule.
/// </summary>
public class UserSession
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ApplicationId { get; set; }
    public string UserId { get; set; } = default!;

    public SessionStatus Status { get; set; }

    /// <summary>K8s deployment name. Stable, derived from session id.</summary>
    public string DeploymentName { get; set; } = default!;
    public string ServiceName { get; set; } = default!;
    public string IngressPath { get; set; } = default!;

    /// <summary>The URL the user opens to reach their app. Built by ingress controller.</summary>
    public string? AccessUrl { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? StoppedAtUtc { get; set; }
    public DateTime LastActivityUtc { get; set; }

    public static UserSession Create(Guid workspaceId, Guid applicationId, string userId)
    {
        var id = Guid.NewGuid();
        var slug = id.ToString("N")[..10];
        return new UserSession
        {
            Id = id,
            WorkspaceId = workspaceId,
            ApplicationId = applicationId,
            UserId = userId,
            Status = SessionStatus.Pending,
            DeploymentName = $"sess-{slug}",
            ServiceName = $"svc-{slug}",
            IngressPath = $"/s/{slug}",
            CreatedAtUtc = DateTime.UtcNow,
            LastActivityUtc = DateTime.UtcNow
        };
    }
}

public enum SessionStatus { Pending, Starting, Running, Stopping, Stopped, Failed }
