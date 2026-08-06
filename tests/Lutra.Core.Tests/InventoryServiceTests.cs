using Lutra.Core.Configuration;
using Lutra.Core.Inventory;

namespace Lutra.Core.Tests;

public sealed class InventoryServiceTests
{
    [Fact]
    public async Task CollectSnapshot_RendersTypedDockerDataWithoutValuesOrStderr()
    {
        const string sentinel = "sentinel-secret-value";
        using var temp = new TempDirectory();
        var runner = new ScriptedRunner((file, arguments) => (file, arguments.FirstOrDefault()) switch
        {
            ("uname", _) => Success("Linux 6.8.0 x86_64 GNU/Linux\n"),
            ("cat", _) => Success("ID=ubuntu\nVERSION_ID=24.04\nPRETTY_NAME=\"Ubuntu 24.04\"\n"),
            ("dpkg-query", _) => Success("curl\t8.5.0\n"),
            ("docker", "ps") => Success("abc123\n"),
            ("docker", "--version") => Success("Docker version 27.0.0\n"),
            ("docker", "inspect") => Success($$"""
                [{
                  "Name": "/app",
                  "Image": "sha256:image-id",
                  "Config": {
                    "Image": "example/app:1.0",
                    "Env": ["API_TOKEN={{sentinel}}", "MODE=production"],
                    "Labels": { "secret": "{{sentinel}}" }
                  },
                  "HostConfig": { "RestartPolicy": { "Name": "unless-stopped" } },
                  "Mounts": [{ "Type": "volume", "Source": "app-data", "Destination": "/data" }],
                  "NetworkSettings": {
                    "Networks": { "frontend": {} },
                    "Ports": { "8080/tcp": [{ "HostIp": "127.0.0.1", "HostPort": "8080" }] }
                  }
                }]
                """),
            ("docker", "network") => Success("frontend\tbridge\n"),
            ("docker", "volume") => Success("app-data\tlocal\n"),
            ("docker", "image") => Success("example/app:1.0\tsha256:digest\n"),
            ("systemctl", "--version") => Success("systemd 255\n"),
            ("systemctl", "list-unit-files") => Success("nginx.service enabled\n"),
            ("systemctl", "list-units") => Success("nginx.service loaded active running\n"),
            _ => new HostProcessResult(1, "", sentinel)
        });
        var service = new InventoryService(CreateConfig(temp), runner);

        var snapshot = await service.CollectSnapshotAsync(new InventoryCollectionPolicy(
            RequirePackages: true, RequireDocker: true, RequireSystemd: true));
        var json = InventoryRenderer.ToJson(snapshot);
        var markdown = InventoryRenderer.ToMarkdown(snapshot);

        Assert.False(snapshot.HasRequiredFailures);
        Assert.Contains("API_TOKEN", json);
        Assert.Contains("MODE", json);
        Assert.Contains("unless-stopped", json);
        Assert.Contains("8080/tcp=127.0.0.1:8080", json);
        Assert.Contains("sha256:digest", json);
        Assert.DoesNotContain(sentinel, json);
        Assert.DoesNotContain(sentinel, markdown);
        Assert.DoesNotContain("Labels", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CollectSnapshot_MalformedRequiredDockerOutputUsesSanitizedFailure()
    {
        const string sentinel = "sentinel-secret-value";
        using var temp = new TempDirectory();
        var runner = new ScriptedRunner((file, arguments) => (file, arguments.FirstOrDefault()) switch
        {
            ("uname", _) => Success("Linux 6.8 x86_64\n"),
            ("cat", _) => Success("ID=test\nVERSION_ID=1\n"),
            ("dpkg-query", _) => Success("curl\t1\n"),
            ("docker", "ps") => Success("abc\n"),
            ("docker", "--version") => Success("Docker 27\n"),
            ("docker", "inspect") => new HostProcessResult(0, "not-json", sentinel),
            ("systemctl", _) => new HostProcessResult(-1, "", sentinel),
            _ => new HostProcessResult(-1, "", sentinel)
        });
        var service = new InventoryService(CreateConfig(temp), runner);

        var snapshot = await service.CollectSnapshotAsync(new InventoryCollectionPolicy(
            RequirePackages: true, RequireDocker: true));
        var docker = Assert.Single(snapshot.Sections, section => section.Name == "docker");
        var serialized = InventoryRenderer.ToJson(snapshot);

        Assert.True(snapshot.HasRequiredFailures);
        Assert.Equal(InventoryCollectorStatus.Failed, docker.Status);
        Assert.Equal("invalid_output", docker.ErrorCategory);
        Assert.DoesNotContain(sentinel, serialized);
        Assert.DoesNotContain("not-json", serialized);
    }

    [Fact]
    public void Renderers_AreDeterministicAndSortSectionsAndEntries()
    {
        var snapshot = new InventorySnapshot
        {
            CapturedAt = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc),
            Host = "host",
            LutraVersion = "1.0",
            Sections =
            [
                new InventorySection
                {
                    Name = "zeta",
                    Required = false,
                    Status = InventoryCollectorStatus.Succeeded,
                    Entries =
                    [
                        new InventoryEntry { Kind = "item", Name = "z" },
                        new InventoryEntry { Kind = "item", Name = "a" }
                    ]
                },
                new InventorySection { Name = "alpha", Required = true, Status = InventoryCollectorStatus.Succeeded }
            ]
        };

        var expectedJson = InventoryRenderer.ToJson(snapshot);
        var reversed = new InventorySnapshot
        {
            CapturedAt = snapshot.CapturedAt,
            Host = snapshot.Host,
            LutraVersion = snapshot.LutraVersion,
            Sections = snapshot.Sections.AsEnumerable().Reverse().Select(section =>
            {
                section.Entries.Reverse();
                return section;
            }).ToList()
        };
        Assert.Equal(expectedJson, InventoryRenderer.ToJson(reversed));
        var markdown = InventoryRenderer.ToMarkdown(snapshot);
        Assert.True(markdown.IndexOf("## alpha", StringComparison.Ordinal) < markdown.IndexOf("## zeta", StringComparison.Ordinal));
        Assert.True(markdown.IndexOf("item: a", StringComparison.Ordinal) < markdown.IndexOf("item: z", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Capture_HonorsConfiguredCollectorSubset()
    {
        using var temp = new TempDirectory();
        var invocations = new List<string>();
        var runner = new ScriptedRunner((file, _) =>
        {
            invocations.Add(file);
            return file switch
            {
                "uname" => Success("Linux 6.8 x86_64\n"),
                "cat" => Success("ID=test\nVERSION_ID=1\n"),
                "ufw" => Success("Status: active\n22/tcp ALLOW Anywhere\n"),
                _ => new HostProcessResult(-1, "", "")
            };
        });
        var config = CreateConfig(temp, new InventoryConfig
        {
            Enabled = true,
            Collectors = ["firewall"]
        });
        var service = new InventoryService(config, runner);

        var result = await service.CaptureAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(["uname", "cat", "ufw"], invocations);
        var markdown = await File.ReadAllTextAsync(result.FilePath!);
        Assert.Contains("## firewall", markdown);
        Assert.DoesNotContain("## packages", markdown);
        Assert.DoesNotContain("## docker", markdown);
        Assert.DoesNotContain("## systemd", markdown);
    }

    private static BackupConfig CreateConfig(TempDirectory temp, InventoryConfig? inventory = null) => new()
    {
        BackupDirectory = Path.Combine(temp.Path, "backups"),
        StateDirectory = Path.Combine(temp.Path, "state"),
        ConfigPath = Path.Combine(temp.Path, "lutra.yaml"),
        Retention = new RetentionPolicy(),
        Inventory = inventory
    };

    private static HostProcessResult Success(string output) => new(0, output, "");

    private sealed class ScriptedRunner(
        Func<string, IReadOnlyList<string>, HostProcessResult> run) : IHostProcessRunner
    {
        public Task<HostProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(run(fileName, arguments));
        }
    }
}
