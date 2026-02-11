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
    private const string DefaultSystemEnvPath = "/etc/lutra/.env";

    public static string ResolveConfigPath(string configPath)
    {
        if (!Environment.IsPrivilegedProcess && configPath == DefaultSystemConfigPath)
            return Path.Combine(ConfigTemplates.GetDefaultConfigDirectory(), "lutra.yaml");

        return configPath;
    }

    public static string ResolveEnvPath(string envFilePath)
    {
        if (!Environment.IsPrivilegedProcess && envFilePath == DefaultSystemEnvPath)
            return Path.Combine(ConfigTemplates.GetDefaultConfigDirectory(), ".env");

        return envFilePath;
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
