using System.Runtime.InteropServices;
using Lutra.Core.Configuration;
using Spectre.Console;

namespace Lutra.CLI.Commands.Config;

/// <summary>
/// Shared helpers for config file path resolution and writing.
/// </summary>
public static class ConfigFileHelper
{
    private const string DefaultSystemConfigPath = "/etc/lutra/lutra.yaml";

    public static (string ConfigPath, string EnvPath) ResolvePaths(
        string? configPath,
        string? envFilePath)
    {
        var resolvedConfigPath = ResolveConfigPath(configPath);
        var resolvedEnvPath = !string.IsNullOrWhiteSpace(envFilePath)
            ? envFilePath
            : Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(resolvedConfigPath))!,
                ".env");

        return (resolvedConfigPath, resolvedEnvPath);
    }

    public static string ResolveConfigPath(string? configPath)
    {
        if (!string.IsNullOrWhiteSpace(configPath))
            return configPath;

        return DefaultSystemConfigPath;
    }

    public static void CreateDirectoryIfNeeded(string path)
    {
        if (Directory.Exists(path))
            return;

        Directory.CreateDirectory(path);
        AnsiConsole.MarkupLine($"  [green]Created[/] directory: {path.EscapeMarkup()}");
    }

    public static bool WriteFile(string path, string content, bool overwrite)
    {
        var exists = File.Exists(path);

        if (exists && !overwrite)
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]Skipped[/] {path.EscapeMarkup()} (already exists, use --force to overwrite)");
            return false;
        }

        File.WriteAllText(path, content);

        var verb = exists ? "Overwritten" : "Created";
        AnsiConsole.MarkupLine($"  [green]{verb}[/] {path.EscapeMarkup()}");
        return true;
    }

    public static void SetEnvFilePermissions(string envFilePath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            File.SetUnixFileMode(envFilePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
            AnsiConsole.MarkupLine($"  Set permissions [blue]600[/] on {envFilePath.EscapeMarkup()}");
        }
    }
}
