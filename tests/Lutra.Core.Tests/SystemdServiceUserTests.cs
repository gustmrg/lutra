using Lutra.CLI.Commands.Schedule;
using Lutra.Core.Configuration;

namespace Lutra.Core.Tests;

public sealed class SystemdServiceUserTests
{
    [Fact]
    public void Resolve_ReturnsNamedNonRootAccountAndHome()
    {
        using var temp = new TempDirectory();
        var passwd = Path.Combine(temp.Path, "passwd");
        File.WriteAllText(passwd, "gustavo:x:1000:1000:Gustavo:/home/gustavo:/bin/bash\n");

        var identity = SystemdServiceUser.Resolve("gustavo", passwd);

        Assert.Equal("gustavo", identity.Name);
        Assert.Equal("/home/gustavo", identity.HomeDirectory);
    }

    [Fact]
    public void Resolve_RequiresExplicitExistingNonRootAccount()
    {
        using var temp = new TempDirectory();
        var passwd = Path.Combine(temp.Path, "passwd");
        File.WriteAllText(passwd, "root:x:0:0:root:/root:/bin/bash\n");

        Assert.Contains(
            "--run-as",
            Assert.Throws<ConfigurationException>(() => SystemdServiceUser.Resolve(null, passwd)).Message);
        Assert.Contains(
            "must not run as root",
            Assert.Throws<ConfigurationException>(() => SystemdServiceUser.Resolve("root", passwd)).Message);
        Assert.Contains(
            "does not exist",
            Assert.Throws<ConfigurationException>(() => SystemdServiceUser.Resolve("gustavo", passwd)).Message);
    }
}
