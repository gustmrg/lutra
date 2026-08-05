using Lutra.CLI.Commands.Config;

namespace Lutra.Core.Tests;

public sealed class ConfigFileHelperTests
{
    [Fact]
    public void ResolvePaths_UsesEnvironmentFileBesideExplicitConfiguration()
    {
        var (configPath, envPath) = ConfigFileHelper.ResolvePaths(
            "/etc/lutra/lutra.yaml",
            null);

        Assert.Equal("/etc/lutra/lutra.yaml", configPath);
        Assert.Equal("/etc/lutra/.env", envPath);
    }

    [Fact]
    public void ResolvePaths_PreservesExplicitEnvironmentFile()
    {
        var (configPath, envPath) = ConfigFileHelper.ResolvePaths(
            "/etc/lutra/lutra.yaml",
            "/run/secrets/lutra.env");

        Assert.Equal("/etc/lutra/lutra.yaml", configPath);
        Assert.Equal("/run/secrets/lutra.env", envPath);
    }
}
