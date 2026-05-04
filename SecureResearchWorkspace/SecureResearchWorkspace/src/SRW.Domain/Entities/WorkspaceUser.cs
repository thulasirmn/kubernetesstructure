using System;

namespace SRW.Domain.Entities;

public class WorkspaceUser
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }

    /// <summary>External identity subject (Keycloak 'sub' claim, etc).</summary>
    public string UserId { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public WorkspaceRole Role { get; set; }
    public DateTime JoinedAtUtc { get; set; }
}
