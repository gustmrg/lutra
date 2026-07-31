using Lutra.Core.Encryption;

namespace Lutra.Core.Configuration;

/// <summary>A named Docker volume archived through a short-lived helper container.</summary>
public sealed class VolumeTarget : IBackupTarget
{
    public required string Name { get; init; }
    public required string Volume { get; init; }
    public string Schedule { get; init; } = "*-*-* 03:00:00";
    public CompressionType Compression { get; init; } = CompressionType.Gzip;
    public RetentionPolicy? Retention { get; init; }
    public EncryptionConfig? Encryption { get; init; }
}
