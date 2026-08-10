using Lutra.CLI.Commands.Schedule;

namespace Lutra.Core.Tests;

public sealed class SystemdServiceUnitTests
{
    [Fact]
    public void Build_QuotesCommandPathsAndSetsExplicitNonRootIdentity()
    {
        var service = SystemdServiceUnit.Build(
            "Lutra backup for app",
            new SystemdServiceUser("gustavo", "/home/gustavo"),
            "/opt/lutra bin/lutra",
            "backup", "run", "--config", "/etc/lutra config/lutra.yaml");

        Assert.Contains("User=gustavo", service);
        Assert.Contains("Environment=\"HOME=/home/gustavo\"", service);
        Assert.Contains(
            "ExecStart=\"/opt/lutra bin/lutra\" \"backup\" \"run\" \"--config\" \"/etc/lutra config/lutra.yaml\"",
            service);
    }
}
