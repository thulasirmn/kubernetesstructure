using System;

namespace SRW.Core.Abstractions;

/// <summary>
/// Accepts session launch requests from the Core layer and provisions the
/// underlying K8s/Terraform resources in the background.
/// Implemented in Infrastructure so Core stays free of DI/hosting concerns.
/// </summary>
public interface ISessionProvisioningQueue
{
    void EnqueueLaunch(Guid sessionId, Guid workspaceId, Guid applicationId, string userId);
}
