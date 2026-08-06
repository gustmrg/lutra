using Lutra.Core.Recovery;

namespace Lutra.Core.Tests;

public sealed class EnvironmentScheduleTests
{
    [Fact]
    public void Build_UsesFixedUnitCommandResolvedPathsAndSchedule()
    {
        var units = EnvironmentScheduleUnits.Build(
            "/opt/lutra bin/lutra",
            "/etc/lutra config/lutra.yaml",
            "/etc/lutra config/.env",
            "Sun *-*-* 01:00:00");

        Assert.Equal("lutra-environment-backup", EnvironmentScheduleUnits.UnitName);
        Assert.Contains(
            "ExecStart=\"/opt/lutra bin/lutra\" environment backup --config \"/etc/lutra config/lutra.yaml\" --env-file \"/etc/lutra config/.env\"",
            units.Service);
        Assert.Contains("OnCalendar=Sun *-*-* 01:00:00", units.Timer);
        Assert.Contains("Persistent=true", units.Timer);
    }
}
