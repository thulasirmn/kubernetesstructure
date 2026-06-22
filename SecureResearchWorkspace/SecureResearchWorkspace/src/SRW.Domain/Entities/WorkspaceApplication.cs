using System;
using System.Collections.Generic;

namespace SRW.Domain.Entities;

/// <summary>
/// A WorkspaceApplication is a launchable application definition (Jupyter, RStudio, custom).
/// It is the *template*; an actual running instance is a UserSession.
/// </summary>
/// <summary>
/// Global application catalog entry. Not scoped to any workspace — workspaces reference
/// these by ID via Workspace.ApplicationIds. Stored in the dedicated "applications" container.
/// </summary>
public class WorkspaceApplication
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public ApplicationType Type { get; set; }

    /// <summary>ACR image, e.g. myregistry.azurecr.io/jupyter:3.0.</summary>
    public string ContainerImage { get; set; } = default!;
    public int ContainerPort { get; set; }

    public string CpuRequest { get; set; } = "100m";
    public string CpuLimit { get; set; } = "2";
    public string MemoryRequest { get; set; } = "1Gi";
    public string MemoryLimit { get; set; } = "4Gi";

    public string MountPath { get; set; } = "/home/jovyan/work";
    public string EnvironmentJson { get; set; } = "{}";
    public string? CommandJson { get; set; }

    /// <summary>False = soft-deleted. Sessions cannot be launched; existing ones are unaffected.</summary>
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
}

public enum ApplicationType { Jupyter, RStudio, Custom }
