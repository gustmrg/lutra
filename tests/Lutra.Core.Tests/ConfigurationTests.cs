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
        Assert.True(config.StateDirectoryWasExplicit);
        Assert.False(config.UsesStateDirectoryCompatibilityFallback);
    }

    [Fact]
    public void Load_MarksCustomCompatibilityFallbackForValidationWarning()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "lutra.yaml");
        File.WriteAllText(path, $$"""
            backup_directory: {{temp.Path}}/backups
            retention:
              max_count: 3
              max_age_days: 7
            files:
              - name: config
                paths: [{{path}}]
                schedule: daily
            """);

        var config = new YamlConfigLoader().Load(path);

        Assert.False(config.StateDirectoryWasExplicit);
        Assert.True(config.UsesStateDirectoryCompatibilityFallback);
        Assert.Equal(Path.Combine(temp.Path, "backups", ".lutra-state"), config.StateDirectory);
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

    [Fact]
    public void Load_ParsesAcknowledgedPlaintextEnvironmentRecovery()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source.txt");
        File.WriteAllText(source, "data");
        var path = WriteEnvironmentConfig(temp, source, """
            environment:
              enabled: true
              acknowledge_plaintext: true
              targets: [config]
              exclude: ["*.token"]
              systemd_units: [nginx.service]
              docker_containers: [app]
            """);

        var config = new YamlConfigLoader().Load(path);

        Assert.NotNull(config.Environment);
        Assert.True(config.Environment.AcknowledgePlaintext);
        Assert.Equal(["config"], config.Environment.Targets);
        Assert.Equal(["*.token"], config.Environment.Exclude);
    }

    [Fact]
    public void Load_RejectsEnabledEnvironmentWithoutPlaintextAcknowledgement()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source.txt");
        File.WriteAllText(source, "data");
        var path = WriteEnvironmentConfig(temp, source, """
            environment:
              enabled: true
              targets: [config]
            """);

        var error = Assert.Throws<ConfigurationException>(() => new YamlConfigLoader().Load(path));

        Assert.Contains("acknowledge_plaintext: true", error.Message);
    }

    [Fact]
    public void Load_RejectsDatabaseEnvironmentTarget()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "lutra.yaml");
        File.WriteAllText(path, $$"""
            backup_directory: {{temp.Path}}/backups
            retention:
              max_count: 3
              max_age_days: 7
            databases:
              - name: db
                type: postgresql
                container: postgres
                database: app
                username: postgres
            environment:
              enabled: true
              acknowledge_plaintext: true
              targets: [db]
            """);

        var error = Assert.Throws<ConfigurationException>(() => new YamlConfigLoader().Load(path));

        Assert.Contains("database target 'db' is not supported", error.Message);
    }

    private static string WriteEnvironmentConfig(TempDirectory temp, string source, string environmentYaml)
    {
        var path = Path.Combine(temp.Path, "lutra.yaml");
        File.WriteAllText(path, $$"""
            backup_directory: {{temp.Path}}/backups
            retention:
              max_count: 3
              max_age_days: 7
            files:
              - name: config
                paths: [{{source}}]
                schedule: daily
            {{environmentYaml}}
            """);
        return path;
    }
}
