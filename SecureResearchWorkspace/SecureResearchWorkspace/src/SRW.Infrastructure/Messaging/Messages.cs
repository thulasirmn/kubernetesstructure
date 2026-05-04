namespace SRW.Infrastructure.Messaging;

public sealed record WorkspaceProvisionMessage(Guid WorkspaceId);

public sealed record SessionStopMessage(Guid SessionId, Guid WorkspaceId);

public sealed record WorkspaceCleanupMessage(Guid WorkspaceId);
