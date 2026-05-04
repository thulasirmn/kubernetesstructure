using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SRW.Core.Abstractions;
using SRW.Domain.Entities;

namespace SRW.Infrastructure.BackgroundJobs;

/// <summary>
/// Runs once per day and reconciles K8s namespaces with workspaces in Cosmos DB.
/// Any namespace with the srw.io/managed-by=srw-api label that has no corresponding
/// Active/Provisioning workspace record is considered orphaned and deleted.
///
/// This catches resources left behind by failed cleanup operations or manual
/// partial deletes, preventing unbounded resource accumulation.
/// </summary>
public sealed class OrphanResourceCleaner : BackgroundService
{
    private readonly IServiceScopeFactory   _scopeFactory;
    private readonly BackgroundJobOptions   _options;
    private readonly ILogger<OrphanResourceCleaner> _log;

    public OrphanResourceCleaner(
        IServiceScopeFactory scopeFactory,
        IOptions<BackgroundJobOptions> options,
        ILogger<OrphanResourceCleaner> log)
    {
        _scopeFactory = scopeFactory;
        _options      = options.Value;
        _log          = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger start so it doesn't fire immediately at pod startup alongside other workers.
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(_options.OrphanCleanerIntervalHours));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await CleanAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "OrphanResourceCleaner tick failed");
            }
        }
    }

    private async Task CleanAsync(CancellationToken ct)
    {
        _log.LogInformation("OrphanResourceCleaner: starting reconciliation sweep");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var workspaceRepo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var k8s           = scope.ServiceProvider.GetRequiredService<IKubernetesOrchestrator>();

        // Collect all workspaces that are in a live state.
        var liveWorkspaces = new HashSet<string>();
        foreach (var status in new[] { WorkspaceStatus.Active, WorkspaceStatus.Provisioning, WorkspaceStatus.Pending })
        {
            var ws = await workspaceRepo.ListByStatusAsync(status, ct);
            foreach (var w in ws)
                liveWorkspaces.Add(w.K8sNamespace);
        }

        // Ask K8s for all namespaces managed by SRW.
        var managedNamespaces = await k8s.ListManagedNamespacesAsync(ct);

        foreach (var ns in managedNamespaces)
        {
            if (!liveWorkspaces.Contains(ns))
            {
                _log.LogWarning("OrphanResourceCleaner: deleting orphaned namespace {Ns}", ns);
                try { await k8s.DeleteWorkspaceNamespaceAsync(ns, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogError(ex, "Failed to delete orphaned namespace {Ns}", ns);
                }
            }
        }

        _log.LogInformation("OrphanResourceCleaner: sweep complete ({Total} managed, {Live} live)",
            managedNamespaces.Count, liveWorkspaces.Count);
    }
}
