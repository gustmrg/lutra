namespace Lutra.CLI.Commands.Uninstall;

/// <summary>Pure preservation rules shared by uninstall execution and tests.</summary>
public static class UninstallDataPolicy
{
    public static bool ShouldRemoveWholeConfigDirectory(string? selectedConfigPath)
        => string.IsNullOrWhiteSpace(selectedConfigPath);

    public static bool ShouldDeleteBackups(bool keepBackups) => !keepBackups;

    public static bool ShouldDeleteState(bool keepBackups, bool keepState)
        => !keepBackups && !keepState;

    public static bool IsSameOrNestedPath(string candidate, string parent)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return normalizedCandidate.Equals(normalizedParent, StringComparison.Ordinal)
            || normalizedCandidate.StartsWith(
                normalizedParent + Path.DirectorySeparatorChar,
                StringComparison.Ordinal);
    }
}
