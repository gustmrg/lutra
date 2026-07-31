using Lutra.Core.Configuration;

namespace Lutra.Core.Backup;

public static class BackupFileNaming
{
    public static string Build(
        string targetName,
        DateTime timestamp,
        string backupId,
        string extension,
        CompressionType compression)
    {
        var name = $"{targetName}_{timestamp:yyyy-MM-dd}_{timestamp:HHmmss}_{backupId}{extension}";
        return compression == CompressionType.Gzip ? name + ".gz" : name;
    }
}
