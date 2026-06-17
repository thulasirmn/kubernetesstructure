using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SRW.Infrastructure.Terraform;

/// <summary>
/// Low-level Terraform shell executor. Manages isolated working directories
/// (one per workspace or session), copies module files on first use, and
/// stores backend config so destroy can re-init without the caller supplying
/// the state key again.
/// </summary>
public sealed class TerraformRunner
{
    private readonly TerraformOptions _opts;
    private readonly ILogger<TerraformRunner> _log;

    public TerraformRunner(IOptions<TerraformOptions> opts, ILogger<TerraformRunner> log)
    {
        _opts = opts.Value;
        _log = log;
    }

    /// <summary>
    /// Returns the isolated working directory path for a given module + key.
    /// The directory is created if it doesn't exist.
    /// </summary>
    public string GetWorkingDir(string module, string key)
    {
        var safeKey = key.Replace("/", "_").Replace(".", "_").Replace("-", "_");
        var dir = Path.Combine(_opts.WorkingRootDir, module, safeKey);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Copies the module's .tf files to the working directory, writes the backend
    /// config to a .tfbackend file for reuse by destroy, then runs terraform init.
    /// Skips init if the working directory was already initialised (has a .terraform/
    /// directory) — saves 20-60 s per repeated operation on the same resource.
    /// </summary>
    public async Task InitAsync(string workingDir, string module, string stateKey, CancellationToken ct)
    {
        CopyModuleFiles(module, workingDir);
        WriteBackendFile(workingDir, stateKey);

        // .terraform/ exists → already initialised for this state key; skip init.
        if (Directory.Exists(Path.Combine(workingDir, ".terraform")))
        {
            _log.LogDebug("Skipping terraform init — already initialised in {Dir}", workingDir);
            return;
        }

        await ExecAsync(workingDir, "init -reconfigure -backend-config=backend.tfbackend", envVars: null, ct);
    }

    /// <summary>
    /// Writes terraform.tfvars and runs terraform apply.
    /// Sensitive values (e.g. storage account key) must be passed via
    /// sensitiveEnvVars as TF_VAR_* — they are never written to disk.
    /// </summary>
    public async Task ApplyAsync(
        string workingDir,
        Dictionary<string, string> vars,
        Dictionary<string, string>? sensitiveEnvVars = null,
        CancellationToken ct = default)
    {
        WriteVarsFile(workingDir, vars);
        await ExecAsync(workingDir, "apply -auto-approve -var-file=terraform.tfvars", sensitiveEnvVars, ct);
    }

    /// <summary>
    /// Runs terraform destroy. Re-initialises only if .terraform/ is missing
    /// (e.g. the pod restarted and the working directory was wiped).
    /// </summary>
    public async Task DestroyAsync(string workingDir, CancellationToken ct = default)
    {
        var backendFilePath = Path.Combine(workingDir, "backend.tfbackend");
        if (!File.Exists(backendFilePath))
            throw new InvalidOperationException(
                $"backend.tfbackend not found in {workingDir} — was InitAsync called?");

        if (!Directory.Exists(Path.Combine(workingDir, ".terraform")))
        {
            _log.LogDebug("Re-initialising before destroy in {Dir}", workingDir);
            await ExecAsync(workingDir, "init -reconfigure -backend-config=backend.tfbackend", envVars: null, ct);
        }

        await ExecAsync(workingDir, "destroy -auto-approve", envVars: null, ct);
    }

    /// <summary>
    /// Parses the JSON from terraform output -json and returns a flat map of
    /// output name → JsonElement value (with sensitive wrappers unwrapped).
    /// </summary>
    public async Task<Dictionary<string, JsonElement>> OutputAsync(
        string workingDir, CancellationToken ct = default)
    {
        var (stdout, _) = await ExecAsync(workingDir, "output -json", envVars: null, ct);
        using var doc = JsonDocument.Parse(stdout);
        return doc.RootElement
            .EnumerateObject()
            .ToDictionary(
                p => p.Name,
                p => p.Value.GetProperty("value").Clone());
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Writes a proper HCL backend config file (.tfbackend) used by -backend-config=.
    /// Avoids shell-quoting fragility of inline -backend-config="key=val" args.
    /// </summary>
    private void WriteBackendFile(string workingDir, string stateKey)
    {
        var content = new StringBuilder();
        content.AppendLine($"storage_account_name = \"{_opts.StateStorageAccount}\"");
        content.AppendLine($"resource_group_name  = \"{_opts.StateResourceGroup}\"");
        content.AppendLine($"container_name       = \"{_opts.StateContainer}\"");
        content.AppendLine($"key                  = \"{stateKey}\"");
        File.WriteAllText(Path.Combine(workingDir, "backend.tfbackend"), content.ToString());
    }

    private void CopyModuleFiles(string module, string workingDir)
    {
        var modulePath = Path.GetFullPath(Path.Combine(_opts.ModulesBasePath, module));
        if (!Directory.Exists(modulePath))
            throw new DirectoryNotFoundException($"Terraform module not found: {modulePath}");

        foreach (var src in Directory.GetFiles(modulePath).Where(f =>
            f.EndsWith(".tf", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".hcl", StringComparison.OrdinalIgnoreCase)))
        {
            File.Copy(src, Path.Combine(workingDir, Path.GetFileName(src)), overwrite: true);
        }
    }

    private static void WriteVarsFile(string workingDir, Dictionary<string, string> vars)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in vars)
            sb.AppendLine($"{k} = \"{EscapeHcl(v)}\"");
        File.WriteAllText(Path.Combine(workingDir, "terraform.tfvars"), sb.ToString());
    }

    private async Task<(string stdout, string stderr)> ExecAsync(
        string workingDir,
        string args,
        Dictionary<string, string>? envVars,
        CancellationToken ct)
    {
        _log.LogDebug("terraform {Args} in {Dir}", args, workingDir);

        var psi = new ProcessStartInfo
        {
            FileName               = _opts.TerraformBinaryPath,
            Arguments              = args,
            WorkingDirectory       = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        // Plugin cache — avoids re-downloading providers on every pod restart.
        psi.Environment["TF_PLUGIN_CACHE_DIR"] = _opts.PluginCacheDir;
        psi.Environment["TF_IN_AUTOMATION"]    = "1";

        // Kubernetes provider reads KUBE_CONFIG_PATH (not KUBECONFIG).
        // In-cluster the provider auto-detects via KUBERNETES_SERVICE_HOST;
        // for local dev we resolve to the standard kubeconfig file.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST")))
            psi.Environment["KUBE_CONFIG_PATH"] = ResolveKubeconfigPath();

        // Pass Azure Workload Identity tokens to the azurerm provider.
        // In AKS the pod already has AZURE_* env vars from the Workload Identity
        // webhook; we map them to the ARM_* names the provider expects.
        MapWorkloadIdentityEnv(psi);

        if (!string.IsNullOrEmpty(_opts.SubscriptionId))
            psi.Environment["ARM_SUBSCRIPTION_ID"] = _opts.SubscriptionId;

        if (envVars is not null)
            foreach (var (k, v) in envVars)
                psi.Environment[k] = v;

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
            // Kill the entire process tree so terraform doesn't continue running
            // in the background after the request is cancelled.
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            _log.LogError("terraform {Args} failed (exit {Code}):\n{Stderr}", args, process.ExitCode, stderr);
            throw new TerraformException($"terraform {args.Split(' ')[0]} failed (exit {process.ExitCode}): {stderr}");
        }

        return (stdout, stderr);
    }

    private static string ResolveKubeconfigPath()
    {
        var envValue = Environment.GetEnvironmentVariable("KUBECONFIG");
        if (!string.IsNullOrEmpty(envValue)) return envValue;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".kube", "config");
    }

    private static void MapWorkloadIdentityEnv(ProcessStartInfo psi)
    {
        var clientId   = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var tenantId   = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
        var tokenFile  = Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE");

        if (clientId is not null && tenantId is not null && tokenFile is not null)
        {
            psi.Environment["ARM_USE_OIDC"]              = "true";
            psi.Environment["ARM_CLIENT_ID"]             = clientId;
            psi.Environment["ARM_TENANT_ID"]             = tenantId;
            psi.Environment["ARM_OIDC_TOKEN_FILE_PATH"]  = tokenFile;
        }
    }

    private static string EscapeHcl(string value)
        => value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "");
}

public sealed class TerraformException : Exception
{
    public TerraformException(string message) : base(message) { }
}
