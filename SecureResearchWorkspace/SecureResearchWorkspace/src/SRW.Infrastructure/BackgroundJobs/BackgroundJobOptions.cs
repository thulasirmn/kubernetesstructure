namespace SRW.Infrastructure.BackgroundJobs;

public sealed class BackgroundJobOptions
{
    /// <summary>How often to poll K8s for Starting/Running session status.</summary>
    public int SessionStatusPollSeconds { get; set; } = 15;

    /// <summary>How often to scan for sessions that have been idle too long.</summary>
    public int IdleReaperIntervalMinutes { get; set; } = 10;

    /// <summary>A session with no activity beyond this threshold is stopped automatically.</summary>
    public int IdleSessionThresholdHours { get; set; } = 8;

    /// <summary>How often to scan for orphaned Azure / K8s resources.</summary>
    public int OrphanCleanerIntervalHours { get; set; } = 24;

    /// <summary>Maximum concurrent workspace provisioning messages processed in parallel.</summary>
    public int ProvisioningMaxConcurrentCalls { get; set; } = 5;

    /// <summary>Maximum message lock renewal window for long-running provisioning (minutes).</summary>
    public int ProvisioningMaxAutoLockRenewMinutes { get; set; } = 10;
}
