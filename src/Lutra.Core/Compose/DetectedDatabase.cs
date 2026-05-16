using Lutra.Core.Configuration;

namespace Lutra.Core.Compose;

public enum DetectionConfidence
{
    High,
    Medium,
    Low
}

public class DetectedDatabase
{
    public required string ServiceName { get; init; }
    public required DatabaseType Type { get; init; }
    public required string ContainerName { get; init; }
    public string? DatabaseName { get; init; }
    public string? Username { get; init; }
    public string? PasswordEnvVar { get; init; }
    public string? ImageName { get; init; }
    public required DetectionConfidence Confidence { get; init; }
}
