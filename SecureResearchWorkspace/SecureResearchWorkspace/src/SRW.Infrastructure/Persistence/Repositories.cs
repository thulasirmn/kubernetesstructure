using Microsoft.Azure.Cosmos;
using SRW.Core.Abstractions;
using SRW.Domain.Entities;

namespace SRW.Infrastructure.Persistence;

// ── Cosmos document models (internal to this file) ────────────────────────────
// Enums are stored as strings for readability. GUIDs are stored as strings.
// CosmosPropertyNamingPolicy.CamelCase maps C# PascalCase props to camelCase JSON,
// so 'Id' → "id", 'WorkspaceId' → "workspaceId" (matching partition key paths).

file sealed class WorkspaceDoc
{
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Description { get; set; } = default!;
    public string Status { get; set; } = default!;
    public string ResourceGroup { get; set; } = default!;
    public string StorageAccountName { get; set; } = default!;
    public string FileShareName { get; set; } = default!;
    public string K8sNamespace { get; set; } = default!;
    public long QuotaInGiB { get; set; } = 100;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ProvisionedAtUtc { get; set; }
    public List<WorkspaceUserDoc> Users { get; set; } = new();
    public List<WorkspaceAppDoc> Applications { get; set; } = new();
    // Denormalized for efficient cross-partition ListForUser queries.
    public List<string> MemberUserIds { get; set; } = new();
}

file sealed class WorkspaceUserDoc
{
    public string Id { get; set; } = default!;
    public string WorkspaceId { get; set; } = default!;
    public string UserId { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string Role { get; set; } = default!;
    public DateTime JoinedAtUtc { get; set; }
}

file sealed class WorkspaceAppDoc
{
    public string Id { get; set; } = default!;
    public string WorkspaceId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Type { get; set; } = default!;
    public string ContainerImage { get; set; } = default!;
    public int ContainerPort { get; set; }
    public string CpuRequest { get; set; } = "500m";
    public string CpuLimit { get; set; } = "2";
    public string MemoryRequest { get; set; } = "1Gi";
    public string MemoryLimit { get; set; } = "4Gi";
    public string MountPath { get; set; } = "/home/jovyan/work";
    public string EnvironmentJson { get; set; } = "{}";
    public string? CommandJson { get; set; }
    public bool Enabled { get; set; } = true;
}

file sealed class SessionDoc
{
    public string Id { get; set; } = default!;
    public string WorkspaceId { get; set; } = default!;
    public string ApplicationId { get; set; } = default!;
    public string UserId { get; set; } = default!;
    public string Status { get; set; } = default!;
    public string DeploymentName { get; set; } = default!;
    public string ServiceName { get; set; } = default!;
    public string IngressPath { get; set; } = default!;
    public string? AccessUrl { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? StoppedAtUtc { get; set; }
    public DateTime LastActivityUtc { get; set; }
}

file sealed class SecretDoc
{
    public string Id { get; set; } = default!;
    public string WorkspaceId { get; set; } = default!;
    public string EncryptedValue { get; set; } = default!;
    public DateTime UpdatedAtUtc { get; set; }
}

// ── Mapping helpers ────────────────────────────────────────────────────────────

file static class WorkspaceMapping
{
    internal static WorkspaceDoc ToDoc(this Workspace w) => new()
    {
        Id                 = w.Id.ToString(),
        Name               = w.Name,
        Description        = w.Description,
        Status             = w.Status.ToString(),
        ResourceGroup      = w.ResourceGroup,
        StorageAccountName = w.StorageAccountName,
        FileShareName      = w.FileShareName,
        K8sNamespace       = w.K8sNamespace,
        QuotaInGiB         = w.QuotaInGiB,
        CreatedAtUtc       = w.CreatedAtUtc,
        ProvisionedAtUtc   = w.ProvisionedAtUtc,
        Users              = w.Users.Select(u => u.ToDoc()).ToList(),
        Applications       = w.Applications.Select(a => a.ToDoc()).ToList(),
        MemberUserIds      = w.Users.Select(u => u.UserId).ToList()
    };

    internal static Workspace ToDomain(this WorkspaceDoc d) =>
        Workspace.Reconstitute(
            id:                 Guid.Parse(d.Id),
            name:               d.Name,
            description:        d.Description,
            status:             Enum.Parse<WorkspaceStatus>(d.Status),
            resourceGroup:      d.ResourceGroup,
            storageAccountName: d.StorageAccountName,
            fileShareName:      d.FileShareName,
            k8sNamespace:       d.K8sNamespace,
            createdAtUtc:       d.CreatedAtUtc,
            provisionedAtUtc:   d.ProvisionedAtUtc,
            users:              d.Users.Select(u => u.ToDomain()).ToList(),
            applications:       d.Applications.Select(a => a.ToDomain()).ToList(),
            quotaInGiB:         d.QuotaInGiB);

    internal static WorkspaceUserDoc ToDoc(this WorkspaceUser u) => new()
    {
        Id          = u.Id.ToString(),
        WorkspaceId = u.WorkspaceId.ToString(),
        UserId      = u.UserId,
        DisplayName = u.DisplayName,
        Role        = u.Role.ToString(),
        JoinedAtUtc = u.JoinedAtUtc
    };

    internal static WorkspaceUser ToDomain(this WorkspaceUserDoc d) => new()
    {
        Id          = Guid.Parse(d.Id),
        WorkspaceId = Guid.Parse(d.WorkspaceId),
        UserId      = d.UserId,
        DisplayName = d.DisplayName,
        Role        = Enum.Parse<WorkspaceRole>(d.Role),
        JoinedAtUtc = d.JoinedAtUtc
    };

    internal static WorkspaceAppDoc ToDoc(this WorkspaceApplication a) => new()
    {
        Id              = a.Id.ToString(),
        WorkspaceId     = a.WorkspaceId.ToString(),
        Name            = a.Name,
        Type            = a.Type.ToString(),
        ContainerImage  = a.ContainerImage,
        ContainerPort   = a.ContainerPort,
        CpuRequest      = a.CpuRequest,
        CpuLimit        = a.CpuLimit,
        MemoryRequest   = a.MemoryRequest,
        MemoryLimit     = a.MemoryLimit,
        MountPath       = a.MountPath,
        EnvironmentJson = a.EnvironmentJson,
        CommandJson     = a.CommandJson,
        Enabled         = a.Enabled
    };

    internal static WorkspaceApplication ToDomain(this WorkspaceAppDoc d) => new()
    {
        Id              = Guid.Parse(d.Id),
        WorkspaceId     = Guid.Parse(d.WorkspaceId),
        Name            = d.Name,
        Type            = Enum.Parse<ApplicationType>(d.Type),
        ContainerImage  = d.ContainerImage,
        ContainerPort   = d.ContainerPort,
        CpuRequest      = d.CpuRequest,
        CpuLimit        = d.CpuLimit,
        MemoryRequest   = d.MemoryRequest,
        MemoryLimit     = d.MemoryLimit,
        MountPath       = d.MountPath,
        EnvironmentJson = d.EnvironmentJson,
        CommandJson     = d.CommandJson,
        Enabled         = d.Enabled
    };
}

file static class SessionMapping
{
    internal static SessionDoc ToDoc(this UserSession s) => new()
    {
        Id              = s.Id.ToString(),
        WorkspaceId     = s.WorkspaceId.ToString(),
        ApplicationId   = s.ApplicationId.ToString(),
        UserId          = s.UserId,
        Status          = s.Status.ToString(),
        DeploymentName  = s.DeploymentName,
        ServiceName     = s.ServiceName,
        IngressPath     = s.IngressPath,
        AccessUrl       = s.AccessUrl,
        CreatedAtUtc    = s.CreatedAtUtc,
        StartedAtUtc    = s.StartedAtUtc,
        StoppedAtUtc    = s.StoppedAtUtc,
        LastActivityUtc = s.LastActivityUtc
    };

    internal static UserSession ToDomain(this SessionDoc d) => new()
    {
        Id              = Guid.Parse(d.Id),
        WorkspaceId     = Guid.Parse(d.WorkspaceId),
        ApplicationId   = Guid.Parse(d.ApplicationId),
        UserId          = d.UserId,
        Status          = Enum.Parse<SessionStatus>(d.Status),
        DeploymentName  = d.DeploymentName,
        ServiceName     = d.ServiceName,
        IngressPath     = d.IngressPath,
        AccessUrl       = d.AccessUrl,
        CreatedAtUtc    = d.CreatedAtUtc,
        StartedAtUtc    = d.StartedAtUtc,
        StoppedAtUtc    = d.StoppedAtUtc,
        LastActivityUtc = d.LastActivityUtc
    };
}

// ── WorkspaceRepository ────────────────────────────────────────────────────────

public class WorkspaceRepository : IWorkspaceRepository
{
    private readonly CosmosContainerProvider _provider;
    public WorkspaceRepository(CosmosContainerProvider provider) => _provider = provider;

    public async Task<Workspace?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var r = await _provider.Workspaces.ReadItemAsync<WorkspaceDoc>(
                id.ToString(), new PartitionKey(id.ToString()), cancellationToken: ct);
            return r.Resource.ToDomain();
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<List<Workspace>> ListForUserAsync(string userId, CancellationToken ct = default)
    {
        // ARRAY_CONTAINS on the denormalized memberUserIds field. Cross-partition fan-out is
        // acceptable here; this query runs only on admin/dashboard screens.
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE ARRAY_CONTAINS(c.memberUserIds, @userId)")
            .WithParameter("@userId", userId);

        var results = new List<Workspace>();
        using var iter = _provider.Workspaces.GetItemQueryIterator<WorkspaceDoc>(query);
        while (iter.HasMoreResults)
            results.AddRange((await iter.ReadNextAsync(ct)).Select(d => d.ToDomain()));
        return results;
    }

    public async Task<List<Workspace>> ListByStatusAsync(WorkspaceStatus status, CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.status = @status")
            .WithParameter("@status", status.ToString());

        var results = new List<Workspace>();
        using var iter = _provider.Workspaces.GetItemQueryIterator<WorkspaceDoc>(query);
        while (iter.HasMoreResults)
            results.AddRange((await iter.ReadNextAsync(ct)).Select(d => d.ToDomain()));
        return results;
    }

    public async Task AddAsync(Workspace workspace, CancellationToken ct = default)
    {
        var doc = workspace.ToDoc();
        await _provider.Workspaces.CreateItemAsync(doc,
            new PartitionKey(doc.Id), cancellationToken: ct);
    }

    public async Task UpdateAsync(Workspace workspace, CancellationToken ct = default)
    {
        var doc = workspace.ToDoc();
        await _provider.Workspaces.UpsertItemAsync(doc,
            new PartitionKey(doc.Id), cancellationToken: ct);
    }

    public async Task<List<WorkspaceApplication>> GetApplicationsAsync(Guid workspaceId, CancellationToken ct = default)
    {
        try
        {
            var r = await _provider.Workspaces.ReadItemAsync<WorkspaceDoc>(
                workspaceId.ToString(), new PartitionKey(workspaceId.ToString()), cancellationToken: ct);
            return r.Resource.Applications.Where(a => a.Enabled).Select(a => a.ToDomain()).ToList();
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new();
        }
    }

    public async Task<WorkspaceApplication?> GetApplicationAsync(Guid applicationId, CancellationToken ct = default)
    {
        // Intra-document JOIN to find an application embedded in any workspace doc.
        // SQL uses camelCase field names because of CosmosPropertyNamingPolicy.CamelCase.
        var query = new QueryDefinition(
            "SELECT VALUE a FROM c JOIN a IN c.applications WHERE a.id = @appId")
            .WithParameter("@appId", applicationId.ToString());

        using var iter = _provider.Workspaces.GetItemQueryIterator<WorkspaceAppDoc>(query);
        while (iter.HasMoreResults)
        {
            var page = await iter.ReadNextAsync(ct);
            var doc = page.FirstOrDefault();
            if (doc is not null) return doc.ToDomain();
        }
        return null;
    }
}

// ── SessionRepository ──────────────────────────────────────────────────────────

public class SessionRepository : ISessionRepository
{
    private readonly CosmosContainerProvider _provider;
    public SessionRepository(CosmosContainerProvider provider) => _provider = provider;

    public async Task<UserSession?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        // Without the partition key (workspaceId) a point read is not possible;
        // fall back to a cross-partition query. This is a low-frequency path (stop/status).
        var query = new QueryDefinition("SELECT * FROM c WHERE c.id = @id")
            .WithParameter("@id", id.ToString());

        using var iter = _provider.Sessions.GetItemQueryIterator<SessionDoc>(query);
        while (iter.HasMoreResults)
        {
            var page = await iter.ReadNextAsync(ct);
            var doc = page.FirstOrDefault();
            if (doc is not null) return doc.ToDomain();
        }
        return null;
    }

    public async Task<UserSession?> GetActiveAsync(Guid wsId, Guid appId, string userId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            @"SELECT TOP 1 * FROM c
              WHERE c.workspaceId = @wsId
                AND c.applicationId = @appId
                AND c.userId = @userId
                AND (c.status = @s1 OR c.status = @s2)
              ORDER BY c.createdAtUtc DESC")
            .WithParameter("@wsId",  wsId.ToString())
            .WithParameter("@appId", appId.ToString())
            .WithParameter("@userId", userId)
            .WithParameter("@s1", SessionStatus.Running.ToString())
            .WithParameter("@s2", SessionStatus.Starting.ToString());

        using var iter = _provider.Sessions.GetItemQueryIterator<SessionDoc>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(wsId.ToString()) });

        if (iter.HasMoreResults)
        {
            var page = await iter.ReadNextAsync(ct);
            return page.FirstOrDefault()?.ToDomain();
        }
        return null;
    }

    public async Task<List<UserSession>> ListForUserAsync(Guid wsId, string userId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.workspaceId = @wsId AND c.userId = @userId ORDER BY c.createdAtUtc DESC")
            .WithParameter("@wsId",   wsId.ToString())
            .WithParameter("@userId", userId);

        var results = new List<UserSession>();
        using var iter = _provider.Sessions.GetItemQueryIterator<SessionDoc>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(wsId.ToString()) });

        while (iter.HasMoreResults)
            results.AddRange((await iter.ReadNextAsync(ct)).Select(d => d.ToDomain()));
        return results;
    }

    public async Task<List<UserSession>> ListByStatusAsync(SessionStatus status, CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.status = @status")
            .WithParameter("@status", status.ToString());

        var results = new List<UserSession>();
        using var iter = _provider.Sessions.GetItemQueryIterator<SessionDoc>(query);
        while (iter.HasMoreResults)
            results.AddRange((await iter.ReadNextAsync(ct)).Select(d => d.ToDomain()));
        return results;
    }

    public async Task<List<UserSession>> ListIdleAsync(TimeSpan idleThreshold, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - idleThreshold;
        var query = new QueryDefinition(
            @"SELECT * FROM c
              WHERE (c.status = @s1 OR c.status = @s2)
                AND c.lastActivityUtc < @cutoff")
            .WithParameter("@s1",     SessionStatus.Running.ToString())
            .WithParameter("@s2",     SessionStatus.Starting.ToString())
            .WithParameter("@cutoff", cutoff);

        var results = new List<UserSession>();
        using var iter = _provider.Sessions.GetItemQueryIterator<SessionDoc>(query);
        while (iter.HasMoreResults)
            results.AddRange((await iter.ReadNextAsync(ct)).Select(d => d.ToDomain()));
        return results;
    }

    public async Task AddAsync(UserSession session, CancellationToken ct = default)
    {
        var doc = session.ToDoc();
        await _provider.Sessions.CreateItemAsync(doc,
            new PartitionKey(doc.WorkspaceId), cancellationToken: ct);
    }

    public async Task UpdateAsync(UserSession session, CancellationToken ct = default)
    {
        var doc = session.ToDoc();
        await _provider.Sessions.UpsertItemAsync(doc,
            new PartitionKey(doc.WorkspaceId), cancellationToken: ct);
    }

    public async Task TouchAsync(Guid sessionId, Guid workspaceId, CancellationToken ct = default)
    {
        await _provider.Sessions.PatchItemAsync<SessionDoc>(
            sessionId.ToString(),
            new PartitionKey(workspaceId.ToString()),
            new[] { PatchOperation.Set("/lastActivityUtc", DateTime.UtcNow) },
            cancellationToken: ct);
    }
}

// ── WorkspaceSecretStore ───────────────────────────────────────────────────────

public class WorkspaceSecretStore : IWorkspaceSecretStore
{
    private readonly CosmosContainerProvider _provider;
    private readonly Microsoft.AspNetCore.DataProtection.IDataProtector _protector;

    public WorkspaceSecretStore(
        CosmosContainerProvider provider,
        Microsoft.AspNetCore.DataProtection.IDataProtectionProvider dpProvider)
    {
        _provider  = provider;
        _protector = dpProvider.CreateProtector("SRW.WorkspaceSecrets.v1");
    }

    public async Task SetStorageKeyAsync(Guid workspaceId, string accountKey, CancellationToken ct = default)
    {
        var encrypted = _protector.Protect(System.Text.Encoding.UTF8.GetBytes(accountKey));
        var doc = new SecretDoc
        {
            Id             = workspaceId.ToString(),
            WorkspaceId    = workspaceId.ToString(),
            EncryptedValue = Convert.ToBase64String(encrypted),
            UpdatedAtUtc   = DateTime.UtcNow
        };
        await _provider.Secrets.UpsertItemAsync(doc,
            new PartitionKey(workspaceId.ToString()), cancellationToken: ct);
    }

    public async Task<string> GetStorageKeyAsync(Guid workspaceId, CancellationToken ct = default)
    {
        try
        {
            var r = await _provider.Secrets.ReadItemAsync<SecretDoc>(
                workspaceId.ToString(), new PartitionKey(workspaceId.ToString()), cancellationToken: ct);
            var bytes = _protector.Unprotect(Convert.FromBase64String(r.Resource.EncryptedValue));
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("No storage key on file for workspace");
        }
    }

    public async Task DeleteAsync(Guid workspaceId, CancellationToken ct = default)
    {
        try
        {
            await _provider.Secrets.DeleteItemAsync<SecretDoc>(
                workspaceId.ToString(), new PartitionKey(workspaceId.ToString()), cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already deleted — treat as success.
        }
    }
}
