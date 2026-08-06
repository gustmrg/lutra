using System.Reflection;
using System.Text.Json;
using Lutra.Core.Backup;
using Lutra.Core.Configuration;

namespace Lutra.Core.Inventory;

/// <summary>Collects and persists secret-conscious host inventory snapshots.</summary>
public interface IInventoryCollector
{
    Task<InventorySnapshot> CollectSnapshotAsync(
        InventoryCollectionPolicy? policy = null,
        CancellationToken cancellationToken = default);
}

public sealed class InventoryService : IInventoryCollector
{
    private static readonly string[] StandaloneCollectors =
        ["packages", "docker", "systemd", "crontabs", "firewall"];

    private readonly BackupConfig _config;
    private readonly IHostProcessRunner _runner;

    public InventoryService(BackupConfig config, IHostProcessRunner? runner = null)
    {
        _config = config;
        _runner = runner ?? new SystemHostProcessRunner();
    }

    public async Task<InventorySnapshot> CollectSnapshotAsync(
        InventoryCollectionPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        policy ??= new InventoryCollectionPolicy();
        var optional = policy.OptionalCollectors ?? [];
        var sections = new List<InventorySection> { await CollectOsAsync(cancellationToken) };
        if (policy.IncludePackages || policy.RequirePackages)
            sections.Add(await CollectPackagesAsync(policy.RequirePackages, cancellationToken));
        if (policy.IncludeDocker || policy.RequireDocker)
            sections.Add(await CollectDockerAsync(policy.RequireDocker, cancellationToken));
        if (policy.IncludeSystemd || policy.RequireSystemd)
            sections.Add(await CollectSystemdAsync(policy.RequireSystemd, cancellationToken));

        if (optional.Contains("crontabs", StringComparer.OrdinalIgnoreCase))
            sections.Add(await CollectCrontabAsync(cancellationToken));
        if (optional.Contains("firewall", StringComparer.OrdinalIgnoreCase))
            sections.Add(await CollectFirewallAsync(cancellationToken));

        return new InventorySnapshot
        {
            CapturedAt = DateTime.UtcNow,
            Host = System.Environment.MachineName,
            LutraVersion = GetVersion(),
            Sections = sections.OrderBy(section => section.Name, StringComparer.Ordinal).ToList()
        };
    }

    public async Task<InventoryResult> CaptureAsync(CancellationToken cancellationToken = default)
    {
        if (_config.Inventory is not { Enabled: true } inventory)
            return new InventoryResult(false, null, "Inventory snapshots are not enabled.");

        var startedAt = DateTime.UtcNow;
        var id = Guid.NewGuid().ToString("N")[..12];
        var directory = Path.Combine(_config.BackupDirectory, "inventory");
        var fileName = $"inventory_{startedAt:yyyy-MM-dd}_{startedAt:HHmmss}_{id}.md";
        var finalPath = Path.Combine(directory, fileName);
        var tempPath = Path.Combine(directory, $".{fileName}.tmp");

        try
        {
            Directory.CreateDirectory(directory);
            await using var targetLock = TargetLock.Acquire(_config.BackupDirectory, "inventory", "Inventory");
            var collectors = inventory.Collectors ?? StandaloneCollectors.ToList();
            var snapshot = await CollectSnapshotAsync(
                new InventoryCollectionPolicy(
                    OptionalCollectors: collectors,
                    IncludePackages: collectors.Contains("packages", StringComparer.OrdinalIgnoreCase),
                    IncludeDocker: collectors.Contains("docker", StringComparer.OrdinalIgnoreCase),
                    IncludeSystemd: collectors.Contains("systemd", StringComparer.OrdinalIgnoreCase)),
                cancellationToken);
            await File.WriteAllTextAsync(tempPath, InventoryRenderer.ToMarkdown(snapshot), cancellationToken);
            File.Move(tempPath, finalPath);

            var checksum = await BackupIntegrity.ComputeSha256Async(finalPath, cancellationToken);
            await BackupIntegrity.WriteChecksumFileAsync(finalPath, checksum, cancellationToken);
            ApplyRetention(directory, _config.Retention);
            return new InventoryResult(true, finalPath, null);
        }
        catch (OperationCanceledException)
        {
            DeleteIfExists(tempPath);
            throw;
        }
        catch
        {
            DeleteIfExists(tempPath);
            return new InventoryResult(false, null, "Inventory collection failed.");
        }
    }

    private async Task<InventorySection> CollectOsAsync(CancellationToken cancellationToken)
    {
        var uname = await _runner.RunAsync("uname", ["-srmo"], cancellationToken);
        var release = await _runner.RunAsync("cat", ["/etc/os-release"], cancellationToken);
        if (!uname.IsSuccess || !release.IsSuccess)
            return Failed("os", required: true, !uname.IsSuccess ? uname : release);

        var attributes = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["architecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            ["kernel"] = FirstLine(uname.StdOut)
        };
        foreach (var line in release.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('=', 2);
            if (parts.Length == 2 && parts[0] is "ID" or "VERSION_ID" or "PRETTY_NAME")
                attributes[parts[0].ToLowerInvariant()] = parts[1].Trim('"');
        }
        return Succeeded("os", required: true, [new InventoryEntry { Kind = "host", Name = "operating-system", Attributes = attributes }]);
    }

    private async Task<InventorySection> CollectPackagesAsync(bool required, CancellationToken cancellationToken)
    {
        foreach (var candidate in new[]
                 {
                     ("dpkg-query", new[] { "-W", "-f=${binary:Package}\t${Version}\\n" }),
                     ("rpm", new[] { "-qa", "--qf", "%{NAME}\\t%{VERSION}-%{RELEASE}\\n" }),
                     ("apk", new[] { "info", "-vv" })
                 })
        {
            var result = await _runner.RunAsync(candidate.Item1, candidate.Item2, cancellationToken);
            if (result.ExitCode == -1)
                continue;
            if (!result.IsSuccess)
                return Failed("packages", required, result);

            var entries = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split('\t', 2))
                .Select(parts => new InventoryEntry
                {
                    Kind = "package",
                    Name = parts[0],
                    Attributes = new SortedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["version"] = parts.Length == 2 ? parts[1] : "installed"
                    }
                })
                .OrderBy(entry => entry.Name, StringComparer.Ordinal)
                .ToList();
            return Succeeded("packages", required, entries);
        }

        return required ? Failed("packages", true, new HostProcessResult(-1, "", "")) : NotApplicable("packages");
    }

    private async Task<InventorySection> CollectDockerAsync(bool required, CancellationToken cancellationToken)
    {
        var ids = await _runner.RunAsync("docker", ["ps", "-aq"], cancellationToken);
        if (ids.ExitCode == -1)
            return required ? Failed("docker", true, ids) : NotApplicable("docker");
        if (!ids.IsSuccess)
            return Failed("docker", required, ids);

        var entries = new List<InventoryEntry>();
        var version = await _runner.RunAsync("docker", ["--version"], cancellationToken);
        if (!version.IsSuccess)
            return Failed("docker", required, version);
        entries.Add(new InventoryEntry
        {
            Kind = "tool",
            Name = "docker",
            Attributes = new() { ["version"] = FirstLine(version.StdOut) }
        });
        var containerIds = ids.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (containerIds.Length > 0)
        {
            var inspect = await _runner.RunAsync("docker", ["inspect", .. containerIds], cancellationToken);
            if (!inspect.IsSuccess)
                return Failed("docker", required, inspect);
            try
            {
                using var json = JsonDocument.Parse(inspect.StdOut);
                foreach (var container in json.RootElement.EnumerateArray())
                    entries.Add(ParseContainer(container));
            }
            catch (JsonException)
            {
                return Failed("docker", required, new HostProcessResult(1, "", ""), "invalid_output");
            }
        }

        if (!await AddDockerListAsync(entries, "network", ["network", "ls", "--format", "{{.Name}}\t{{.Driver}}"], cancellationToken)
            || !await AddDockerListAsync(entries, "volume", ["volume", "ls", "--format", "{{.Name}}\t{{.Driver}}"], cancellationToken)
            || !await AddDockerListAsync(entries, "image", ["image", "ls", "--digests", "--format", "{{.Repository}}:{{.Tag}}\t{{.Digest}}"], cancellationToken))
            return Failed("docker", required, new HostProcessResult(1, "", ""));
        return Succeeded("docker", required, entries.OrderBy(entry => entry.Kind).ThenBy(entry => entry.Name).ToList());
    }

    private async Task<InventorySection> CollectSystemdAsync(bool required, CancellationToken cancellationToken)
    {
        var version = await _runner.RunAsync("systemctl", ["--version"], cancellationToken);
        if (version.ExitCode == -1)
            return required ? Failed("systemd", true, version) : NotApplicable("systemd");
        var enabled = await _runner.RunAsync(
            "systemctl", ["list-unit-files", "--state=enabled", "--no-pager", "--no-legend"], cancellationToken);
        var running = await _runner.RunAsync(
            "systemctl", ["list-units", "--type=service", "--state=running", "--no-pager", "--no-legend"], cancellationToken);
        if (!version.IsSuccess || !enabled.IsSuccess || !running.IsSuccess)
            return Failed("systemd", required, !version.IsSuccess ? version : !enabled.IsSuccess ? enabled : running);

        var entries = new List<InventoryEntry>
        {
            new() { Kind = "tool", Name = "systemd", Attributes = new() { ["version"] = FirstLine(version.StdOut) } }
        };
        entries.AddRange(ParseUnitLines(enabled.StdOut, "enabled-unit"));
        entries.AddRange(ParseUnitLines(running.StdOut, "running-service"));
        return Succeeded("systemd", required, entries);
    }

    private async Task<InventorySection> CollectCrontabAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync("crontab", ["-l"], cancellationToken);
        if (result.ExitCode == -1)
            return NotApplicable("crontabs");
        if (!result.IsSuccess)
            return Succeeded("crontabs", false, []);

        var entries = new List<InventoryEntry>();
        foreach (var raw in result.StdOut.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var fields = line.Split((char[]?)null, 6, StringSplitOptions.RemoveEmptyEntries);
            var schedule = line.StartsWith('@') ? fields[0] : fields.Length >= 5 ? string.Join(' ', fields.Take(5)) : "unknown";
            entries.Add(new InventoryEntry { Kind = "cron-schedule", Name = schedule });
        }
        return Succeeded("crontabs", false, entries);
    }

    private async Task<InventorySection> CollectFirewallAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync("ufw", ["status", "verbose"], cancellationToken);
        if (result.ExitCode == -1)
            return NotApplicable("firewall");
        if (!result.IsSuccess)
            return Failed("firewall", false, result);

        var entries = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('#', 2)[0].Trim())
            .Where(line => line.Length > 0)
            .Select((line, index) => new InventoryEntry { Kind = "firewall-state", Name = $"line-{index + 1}", Attributes = new() { ["value"] = line } })
            .ToList();
        return Succeeded("firewall", false, entries);
    }

    private async Task<bool> AddDockerListAsync(
        List<InventoryEntry> entries,
        string kind,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync("docker", arguments, cancellationToken);
        if (!result.IsSuccess)
            return false;
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t', 2);
            entries.Add(new InventoryEntry
            {
                Kind = kind,
                Name = parts[0],
                Attributes = parts.Length == 2 ? new() { [kind == "image" ? "digest" : "driver"] = parts[1] } : new()
            });
        }
        return true;
    }

    private static InventoryEntry ParseContainer(JsonElement container)
    {
        var name = GetString(container, "Name")?.TrimStart('/') ?? "unknown";
        var config = container.TryGetProperty("Config", out var configValue) ? configValue : default;
        var attributes = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["image"] = GetString(config, "Image") ?? "unknown",
            ["image_id"] = GetString(container, "Image") ?? "unknown"
        };
        if (config.ValueKind != JsonValueKind.Undefined
            && config.TryGetProperty("Env", out var environment)
            && environment.ValueKind == JsonValueKind.Array)
        {
            attributes["environment_names"] = string.Join(',', environment.EnumerateArray()
                .Select(value => value.GetString()?.Split('=', 2)[0])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Order(StringComparer.Ordinal));
        }
        if (container.TryGetProperty("HostConfig", out var host)
            && host.TryGetProperty("RestartPolicy", out var restart))
            attributes["restart_policy"] = GetString(restart, "Name") ?? "none";
        if (container.TryGetProperty("Mounts", out var mounts) && mounts.ValueKind == JsonValueKind.Array)
            attributes["mounts"] = string.Join(';', mounts.EnumerateArray().Select(mount =>
                $"{GetString(mount, "Type")}:{GetString(mount, "Source")}->{GetString(mount, "Destination")}"));
        if (container.TryGetProperty("NetworkSettings", out var networkSettings)
            && networkSettings.TryGetProperty("Networks", out var networks))
            attributes["networks"] = string.Join(',', networks.EnumerateObject().Select(network => network.Name).Order());
        if (container.TryGetProperty("NetworkSettings", out networkSettings)
            && networkSettings.TryGetProperty("Ports", out var ports)
            && ports.ValueKind == JsonValueKind.Object)
        {
            attributes["ports"] = string.Join(';', ports.EnumerateObject()
                .OrderBy(port => port.Name, StringComparer.Ordinal)
                .Select(port => FormatPort(port.Name, port.Value)));
        }
        return new InventoryEntry { Kind = "container", Name = name, Attributes = attributes };
    }

    private static string FormatPort(string containerPort, JsonElement bindings)
    {
        if (bindings.ValueKind != JsonValueKind.Array)
            return containerPort;
        var published = bindings.EnumerateArray()
            .Select(binding => $"{GetString(binding, "HostIp") ?? ""}:{GetString(binding, "HostPort") ?? ""}")
            .Order(StringComparer.Ordinal);
        return $"{containerPort}={string.Join(',', published)}";
    }

    private static IEnumerable<InventoryEntry> ParseUnitLines(string output, string kind)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => new InventoryEntry { Kind = kind, Name = name! });

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static InventorySection Succeeded(string name, bool required, List<InventoryEntry> entries)
        => new() { Name = name, Status = InventoryCollectorStatus.Succeeded, Required = required, ExitCode = 0, Entries = entries };

    private static InventorySection Failed(
        string name,
        bool required,
        HostProcessResult result,
        string? category = null)
        => new()
        {
            Name = name,
            Status = InventoryCollectorStatus.Failed,
            Required = required,
            ExitCode = result.ExitCode,
            ErrorCategory = category ?? (result.ExitCode == -1 ? "tool_unavailable" : "command_failed")
        };

    private static InventorySection NotApplicable(string name)
        => new() { Name = name, Status = InventoryCollectorStatus.NotApplicable, Required = false };

    private static string FirstLine(string value)
        => value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "unknown";

    private static void ApplyRetention(string directory, RetentionPolicy retention)
    {
        var files = Directory.GetFiles(directory, "inventory_*.md")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();
        var cutoff = DateTime.UtcNow.AddDays(-retention.MaxAgeDays);
        var candidates = files.Select((file, index) => new
            {
                File = file,
                Index = index,
                CountExceeded = index >= retention.MaxCount,
                AgeExceeded = file.LastWriteTimeUtc < cutoff
            })
            .Where(item => item.Index >= retention.KeepAtLeast)
            .Where(item => retention.Mode == RetentionMode.Both
                ? item.CountExceeded && item.AgeExceeded
                : item.CountExceeded || item.AgeExceeded);
        foreach (var candidate in candidates)
        {
            File.Delete(candidate.File.FullName);
            DeleteIfExists(BackupIntegrity.GetChecksumPath(candidate.File.FullName));
        }
    }

    private static string GetVersion()
        => typeof(InventoryService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? typeof(InventoryService).Assembly.GetName().Version?.ToString()
           ?? "unknown";

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

public sealed record InventoryResult(bool Success, string? FilePath, string? ErrorMessage);
