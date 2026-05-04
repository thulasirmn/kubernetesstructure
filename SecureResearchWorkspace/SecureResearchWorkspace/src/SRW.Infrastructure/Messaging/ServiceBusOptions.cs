using Azure.Identity;
using Azure.Messaging.ServiceBus;

namespace SRW.Infrastructure.Messaging;

public sealed class ServiceBusOptions
{
    /// <summary>Fully qualified Service Bus namespace, e.g. srw-servicebus.servicebus.windows.net</summary>
    public string FullyQualifiedNamespace { get; set; } = default!;

    /// <summary>
    /// Optional. If set, used instead of FullyQualifiedNamespace + DefaultAzureCredential.
    /// Use a connection string for local development when Managed Identity / RBAC isn't configured.
    /// On AKS, leave this empty so the workload identity picks up the Service Bus Data Owner role.
    /// </summary>
    public string? ConnectionString { get; set; }

    public string WorkspaceProvisionQueue { get; set; } = "srw-workspace-provision";
    public string SessionStopQueue        { get; set; } = "srw-session-stop";
    public string WorkspaceCleanupQueue   { get; set; } = "srw-workspace-cleanup";
}

internal static class ServiceBusClientFactory
{
    public static ServiceBusClient Create(ServiceBusOptions options) =>
        string.IsNullOrWhiteSpace(options.ConnectionString)
            ? new ServiceBusClient(options.FullyQualifiedNamespace, new DefaultAzureCredential())
            : new ServiceBusClient(options.ConnectionString);
}
