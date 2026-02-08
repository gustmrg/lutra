namespace Lutra.Core.Configuration;

/// <summary>
/// Specifies the compression algorithm to apply to backup files.
/// </summary>
public enum CompressionType
{
    /// <summary>
    /// No compression. Backup files are stored as-is.
    /// </summary>
    None,

    /// <summary>
    /// Gzip compression. Backup files have a <c>.gz</c> extension appended.
    /// </summary>
    /// <remarks>
    /// This is the default compression type. Provides good compression ratios
    /// for database dumps with reasonable CPU overhead.
    /// </remarks>
    Gzip
}
