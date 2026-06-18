namespace SRW.Infrastructure.Helm;

public sealed class HelmOptions
{
    public string HelmBinaryPath { get; set; } = "helm";

    /// <summary>
    /// Path to the session Helm chart. Relative paths are resolved from the process working directory.
    /// In production (K8s pod) set to the absolute path where the chart is copied in the Docker image.
    /// </summary>
    public string SessionChartPath { get; set; } = "charts/session";
}
