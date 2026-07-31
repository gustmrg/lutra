namespace Lutra.Core.Sync;

public sealed class RsyncConfig
{
    public string Type { get; init; } = "rsync";
    public required string Host { get; init; }
    public required string User { get; init; }
    public required string DestinationPath { get; init; }
    public required string SshKeyPath { get; init; }
    public int Port { get; init; } = 22;
    public List<string> ExtraArgs { get; init; } = [];
    public bool Delete { get; init; }
    public bool PostBackup { get; init; }
}
