using Lutra.Core.Configuration;

namespace Lutra.Core.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void StateDirectoryResolution_CoversExplicitSystemAndCompatibilityPaths()
    {
        var explicitPath = YamlConfigLoader.ResolveStateDirectory(
            "/srv/lutra-state", "/srv/backups", "/opt/lutra/config.yaml");
        var systemPath = YamlConfigLoader.ResolveStateDirectory(
            null, "/var/backups/lutra", "/etc/lutra/custom/config.yaml");
        var compatibilityPath = YamlConfigLoader.ResolveStateDirectory(
            null, "backups", "/opt/lutra/config.yaml");

        Assert.Equal("/srv/lutra-state", explicitPath);
        Assert.Equal("/var/lib/lutra", systemPath);
        Assert.Equal("/opt/lutra/backups/.lutra-state", compatibilityPath);
    }

    [Fact]
    public void NewUserStateDirectory_UsesAbsoluteXdgOrHomeFallback()
    {
        Assert.Equal(
            "/srv/user-state/lutra",
            ConfigTemplates.ResolveDefaultStateDirectory(false, "/srv/user-state", "/home/lutra"));
        Assert.Equal(
            "/home/lutra/.local/state/lutra",
            ConfigTemplates.ResolveDefaultStateDirectory(false, "relative/state", "/home/lutra"));
        Assert.Equal(
            "/var/lib/lutra",
            ConfigTemplates.ResolveDefaultStateDirectory(true, "/ignored", "/ignored"));
    }

    [Fact]
    public void Load_ResolvesExplicitRelativeStateDirectoryFromConfigDirectory()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "lutra.yaml");
        File.WriteAllText(path, $$"""
            backup_directory: backups
            state_directory: state
            retention:
              max_count: 3
              max_age_days: 7
            files:
              - name: config
                paths: [{{path}}]
                schedule: daily
            """);

        var config = new YamlConfigLoader().Load(path);

        Assert.Equal(Path.Combine(temp.Path, "state"), config.StateDirectory);
        Assert.Equal(Path.GetFullPath(path), config.ConfigPath);
    }

    [Fact]
    public void Load_RejectsDuplicateTargetNamesAcrossKinds()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source.txt");
        File.WriteAllText(source, "data");
        var yaml = $$"""
            backup_directory: {{temp.Path}}/backups
            retention:
              max_count: 10
              max_age_days: 30
            databases:
              - name: duplicate
                type: postgresql
                container: pg
                database: app
                username: postgres
                schedule: daily
            files:
              - name: duplicate
                paths: [{{source}}]
                schedule: daily
            """;
        var path = Path.Combine(temp.Path, "lutra.yaml");
        File.WriteAllText(path, yaml);

        var error = Assert.Throws<ConfigurationException>(() => new YamlConfigLoader().Load(path));
        Assert.Contains("duplicate target name", error.Message);
    }

    [Fact]
    public void Load_ParsesRetentionModeAndKeepMinimum()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source.txt");
        File.WriteAllText(source, "data");
        var path = Path.Combine(temp.Path, "lutra.yaml");
        File.WriteAllText(path, $$"""
            backup_directory: {{temp.Path}}/backups
            retention:
              max_count: 3
              max_age_days: 7
              mode: either
              keep_at_least: 2
            files:
              - name: config
                paths: [{{source}}]
                schedule: daily
            """);

        var config = new YamlConfigLoader().Load(path);

        Assert.Equal(RetentionMode.Either, config.Retention.Mode);
        Assert.Equal(2, config.Retention.KeepAtLeast);
    }
}
