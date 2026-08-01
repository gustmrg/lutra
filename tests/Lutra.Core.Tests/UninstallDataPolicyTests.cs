using Lutra.CLI.Commands.Uninstall;

namespace Lutra.Core.Tests;

public sealed class UninstallDataPolicyTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void StateDeletion_HonorsBothPreservationFlags(
        bool keepBackups,
        bool keepState,
        bool expected)
    {
        Assert.Equal(
            expected,
            UninstallDataPolicy.ShouldDeleteState(keepBackups, keepState));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void BackupDeletion_HonorsPreservationFlag(bool keepBackups, bool expected)
    {
        Assert.Equal(expected, UninstallDataPolicy.ShouldDeleteBackups(keepBackups));
    }

    [Fact]
    public void NestedState_IsDetectedSoPreservationCannotDeleteItsParent()
    {
        using var temp = new TempDirectory();
        var backupDirectory = Path.Combine(temp.Path, "backups");

        Assert.True(UninstallDataPolicy.IsSameOrNestedPath(
            Path.Combine(backupDirectory, ".lutra-state"),
            backupDirectory));
        Assert.False(UninstallDataPolicy.IsSameOrNestedPath(
            Path.Combine(temp.Path, "state"),
            backupDirectory));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("/srv/custom/lutra.yaml", false)]
    public void CustomConfig_RemovesOnlyTheSelectedFile(string? selectedPath, bool expected)
    {
        Assert.Equal(
            expected,
            UninstallDataPolicy.ShouldRemoveWholeConfigDirectory(selectedPath));
    }
}
