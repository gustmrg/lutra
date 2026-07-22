namespace Lutra.Core.Backup;

/// <summary>
/// Provides per-target file locking so that backup and restore operations
/// for the same target cannot run concurrently.
/// </summary>
internal static class TargetLock
{
    /// <summary>
    /// Acquires an exclusive lock for the given target. The returned stream
    /// must be disposed to release the lock.
    /// </summary>
    /// <param name="backupDirectory">The configured backup directory.</param>
    /// <param name="targetName">The target name to lock.</param>
    /// <param name="operation">The operation name used in the error message (e.g. "Backup").</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when another operation already holds the lock for this target.
    /// </exception>
    public static FileStream Acquire(string backupDirectory, string targetName, string operation)
    {
        var lockDir = Path.Combine(backupDirectory, ".locks");
        Directory.CreateDirectory(lockDir);

        var lockPath = Path.Combine(lockDir, SanitizeFileComponent(targetName) + ".lock");
        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            LockFile(stream);
            return stream;
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"{operation} for target '{targetName}' is already running.", ex);
        }
    }

    private static string SanitizeFileComponent(string value)
    {
        return new string(value.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
    }

    private static void LockFile(FileStream stream)
    {
        if (!OperatingSystem.IsMacOS())
            stream.Lock(0, 0);
    }
}
