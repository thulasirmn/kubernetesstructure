using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace SRW.Infrastructure.Persistence;

/// <summary>
/// Singleton that owns the CosmosClient and exposes typed Container references.
/// Call InitializeAsync() once at startup to create the database and containers if they don't exist.
///
/// Containers:
///   workspaces   — partition key /id           (workspace doc embeds users; holds applicationIds list)
///   sessions     — partition key /workspaceId
///   secrets      — partition key /workspaceId  (encrypted storage account keys)
///   applications — partition key /id           (global catalog; workspaces reference by ID)
/// </summary>
public sealed class CosmosContainerProvider : IAsyncDisposable
{
    private readonly CosmosClient _client;
    private const string DatabaseId              = "srw";
    internal const string WorkspacesContainer    = "workspaces";
    internal const string SessionsContainer      = "sessions";
    internal const string SecretsContainer       = "secrets";
    internal const string ApplicationsContainer  = "applications";

    public CosmosContainerProvider(IOptions<CosmosDbOptions> opts)
    {
        var o = opts.Value;
        var clientOpts = new CosmosClientOptions
        {
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
            }
        };

        _client = string.IsNullOrEmpty(o.AccountKey)
            ? new CosmosClient(o.Endpoint, new DefaultAzureCredential(), clientOpts)
            : new CosmosClient(o.Endpoint, o.AccountKey, clientOpts);
    }

    public Container Workspaces    => _client.GetContainer(DatabaseId, WorkspacesContainer);
    public Container Sessions      => _client.GetContainer(DatabaseId, SessionsContainer);
    public Container Secrets       => _client.GetContainer(DatabaseId, SecretsContainer);
    public Container Applications  => _client.GetContainer(DatabaseId, ApplicationsContainer);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var db = (await _client.CreateDatabaseIfNotExistsAsync(DatabaseId, cancellationToken: ct)).Database;
        await db.CreateContainerIfNotExistsAsync(WorkspacesContainer,   "/id",          cancellationToken: ct);
        await db.CreateContainerIfNotExistsAsync(SessionsContainer,     "/workspaceId", cancellationToken: ct);
        await db.CreateContainerIfNotExistsAsync(SecretsContainer,      "/workspaceId", cancellationToken: ct);
        await db.CreateContainerIfNotExistsAsync(ApplicationsContainer, "/id",          cancellationToken: ct);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class CosmosDbOptions
{
    public string Endpoint { get; set; } = default!;
    /// <summary>Leave empty on AKS to use DefaultAzureCredential (Managed Identity).</summary>
    public string? AccountKey { get; set; }
}
