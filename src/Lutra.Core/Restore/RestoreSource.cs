namespace Lutra.Core.Restore;

/// <summary>
/// Describes a backup file being used as a restore source.
/// </summary>
/// <param name="FilePath">The absolute path of the backup file on the host.</param>
/// <param name="Extension">
/// The backup format extension including the leading dot, excluding any compression
/// suffix (e.g. <c>.dump</c>, <c>.sql</c>).
/// </param>
/// <param name="IsCompressed">Whether the file is gzip-compressed.</param>
public record RestoreSource(string FilePath, string Extension, bool IsCompressed);
