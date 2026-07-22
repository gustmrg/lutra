using System.Formats.Tar;
using System.IO.Compression;
using Lutra.Core.Configuration;

namespace Lutra.Core.Files;

/// <summary>
/// Creates, inspects, and extracts tar archives for file/configuration backup targets.
/// Archive entry names are stored relative to the filesystem root (without the leading
/// <c>/</c>), so extracting an archive into <c>/</c> restores files to their original
/// locations.
/// </summary>
public static class FileArchive
{
    /// <summary>
    /// Creates a tar archive (optionally gzip-compressed) containing the given files
    /// and directories.
    /// </summary>
    /// <param name="paths">Files and directories to include. Directories are archived recursively.</param>
    /// <param name="excludePatterns">Glob-style exclude patterns (see <see cref="FileTarget.Exclude"/>).</param>
    /// <param name="outputPath">Destination file path on the host.</param>
    /// <param name="compression">Whether to gzip-compress the archive.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <exception cref="FileNotFoundException">A configured path does not exist.</exception>
    public static async Task CreateAsync(
        IReadOnlyList<string> paths,
        IReadOnlyList<string> excludePatterns,
        string outputPath,
        CompressionType compression,
        CancellationToken cancellationToken = default)
    {
        await using var fileStream = new FileStream(
            outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        await using var gzipStream = compression == CompressionType.Gzip
            ? new GZipStream(fileStream, CompressionLevel.Optimal)
            : null;

        var tarStream = (Stream?)gzipStream ?? fileStream;
        await using var writer = new TarWriter(tarStream, TarEntryFormat.Pax, leaveOpen: false);

        foreach (var path in paths)
        {
            var fullPath = Path.GetFullPath(path);

            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                throw new FileNotFoundException($"Configured path does not exist: {fullPath}", fullPath);

            await AddPathAsync(writer, fullPath, excludePatterns, cancellationToken);
        }
    }

    /// <summary>
    /// Reads through the archive, validating its integrity, and returns the number
    /// of entries it contains. Corrupt archives cause an exception.
    /// </summary>
    public static async Task<long> CountEntriesAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        await using var stream = OpenReadMaybeCompressed(archivePath);
        using var reader = new TarReader(stream);

        long count = 0;
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is not null)
            count++;

        return count;
    }

    /// <summary>
    /// Extracts the archive into <paramref name="destinationDirectory"/>, overwriting
    /// existing files. Pass <c>/</c> to restore files to their original locations.
    /// Entries attempting to escape the destination are rejected by the extractor.
    /// </summary>
    public static async Task ExtractAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        await using var stream = OpenReadMaybeCompressed(archivePath);
        await TarFile.ExtractToDirectoryAsync(stream, destinationDirectory, overwriteFiles: true, cancellationToken);
    }

    private static async Task AddPathAsync(
        TarWriter writer,
        string fullPath,
        IReadOnlyList<string> excludePatterns,
        CancellationToken cancellationToken)
    {
        var entryName = ToEntryName(fullPath);

        if (Directory.Exists(fullPath))
        {
            if (IsExcluded(entryName + "/", excludePatterns))
                return;

            var dirEntry = new PaxTarEntry(TarEntryType.Directory, entryName + "/");
            ApplyMetadata(dirEntry, fullPath);
            await writer.WriteEntryAsync(dirEntry, cancellationToken);

            foreach (var child in Directory.EnumerateFileSystemEntries(fullPath).Order(StringComparer.Ordinal))
                await AddPathAsync(writer, child, excludePatterns, cancellationToken);

            return;
        }

        if (IsExcluded(entryName, excludePatterns))
            return;

        await using var dataStream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
        {
            DataStream = dataStream
        };
        ApplyMetadata(entry, fullPath);
        await writer.WriteEntryAsync(entry, cancellationToken);
    }

    private static string ToEntryName(string fullPath)
    {
        var trimmed = fullPath.TrimStart(Path.DirectorySeparatorChar);
        return trimmed.Length > 0 ? trimmed : ".";
    }

    private static bool IsExcluded(string entryName, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (GlobMatcher.IsMatch(pattern, entryName))
                return true;

            // Also match when any single path segment matches the pattern,
            // so "node_modules" excludes any directory with that name.
            if (entryName.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Any(segment => GlobMatcher.IsMatch(pattern, segment)))
                return true;
        }

        return false;
    }

    private static void ApplyMetadata(PaxTarEntry entry, string fullPath)
    {
        try
        {
            entry.ModificationTime = File.GetLastWriteTimeUtc(fullPath);

            if (OperatingSystem.IsLinux())
                entry.Mode = File.GetUnixFileMode(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Metadata is best-effort; content matters more than permissions here.
        }
    }

    private static Stream OpenReadMaybeCompressed(string archivePath)
    {
        var fileStream = new FileStream(
            archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

        return archivePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(fileStream, CompressionMode.Decompress)
            : fileStream;
    }
}
