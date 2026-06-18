using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SRW.Infrastructure.Helm;

/// <summary>
/// Thin wrapper around the Helm CLI for session lifecycle operations.
/// Stateless — no working directories, no state backend, no init step.
/// </summary>
public sealed class HelmRunner
{
    private readonly HelmOptions _opts;
    private readonly ILogger<HelmRunner> _log;

    public HelmRunner(IOptions<HelmOptions> opts, ILogger<HelmRunner> log)
    {
        _opts = opts.Value;
        _log  = log;
    }

    /// <summary>
    /// Runs: helm upgrade --install &lt;release&gt; &lt;chart&gt; -n &lt;namespace&gt; -f &lt;values-file&gt;
    /// Idempotent — creates or updates. Values are written as JSON (Helm 3 accepts JSON values files).
    /// </summary>
    public async Task InstallOrUpgradeAsync(
        string releaseName,
        string k8sNamespace,
        string valuesJson,
        CancellationToken ct)
    {
        var valuesFile = Path.Combine(Path.GetTempPath(), $"srw-helm-{releaseName}.json");
        await File.WriteAllTextAsync(valuesFile, valuesJson, ct);
        try
        {
            var args = $"upgrade --install {releaseName} {_opts.SessionChartPath} -n {k8sNamespace} -f \"{valuesFile}\" --atomic=false --timeout 5m";
            await ExecAsync(args, ct);
        }
        finally
        {
            try { File.Delete(valuesFile); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Runs: helm uninstall &lt;release&gt; -n &lt;namespace&gt;
    /// Swallows "not found" — calling uninstall on a missing release is treated as success.
    /// </summary>
    public async Task UninstallAsync(string releaseName, string k8sNamespace, CancellationToken ct)
    {
        try
        {
            await ExecAsync($"uninstall {releaseName} -n {k8sNamespace}", ct);
        }
        catch (HelmException ex) when (
            ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("release: not found", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogDebug("Helm release {Release} not found — treating as already uninstalled", releaseName);
        }
    }

    private async Task ExecAsync(string args, CancellationToken ct)
    {
        _log.LogDebug("helm {Args}", args);

        var psi = new ProcessStartInfo
        {
            FileName               = _opts.HelmBinaryPath,
            Arguments              = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            _log.LogError("helm {Args} failed (exit {Code}):\n{Stderr}", args, process.ExitCode, stderr);
            throw new HelmException($"helm {args.Split(' ')[0]} failed (exit {process.ExitCode}): {stderr}");
        }

        if (!string.IsNullOrWhiteSpace(stdout))
            _log.LogDebug("helm output: {Stdout}", stdout);
    }
}

public sealed class HelmException : Exception
{
    public HelmException(string message) : base(message) { }
}
