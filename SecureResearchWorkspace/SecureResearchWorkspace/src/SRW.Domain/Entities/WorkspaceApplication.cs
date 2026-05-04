using System;
using System.Collections.Generic;

namespace SRW.Domain.Entities;

/// <summary>
/// A WorkspaceApplication is a launchable application definition (Jupyter, RStudio, custom).
/// It is the *template*; an actual running instance is a UserSession.
/// </summary>
public class WorkspaceApplication
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = default!;
    public ApplicationType Type { get; set; }

    /// <summary>Container image, e.g. jupyter/scipy-notebook:latest.</summary>
    public string ContainerImage { get; set; } = default!;
    public int ContainerPort { get; set; }

    /// <summary>Resource limits per user pod.</summary>
    public string CpuRequest { get; set; } = "500m";
    public string CpuLimit { get; set; } = "2";
    public string MemoryRequest { get; set; } = "1Gi";
    public string MemoryLimit { get; set; } = "4Gi";

    /// <summary>Mount path inside the container where the workspace file share is mounted.</summary>
    public string MountPath { get; set; } = "/home/jovyan/work";

    /// <summary>Environment variables, JSON-encoded.</summary>
    public string EnvironmentJson { get; set; } = "{}";

    /// <summary>Container start command override (optional). JSON array.</summary>
    public string? CommandJson { get; set; }

    public bool Enabled { get; set; } = true;
}

public enum ApplicationType { Jupyter, RStudio, Custom }
