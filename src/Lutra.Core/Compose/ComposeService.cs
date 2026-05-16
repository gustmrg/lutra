namespace Lutra.Core.Compose;

public class ComposeService
{
    public required string ServiceName { get; init; }
    public string? Image { get; init; }
    public string? ContainerName { get; init; }
    public Dictionary<string, string> Environment { get; init; } = new();
    public List<string> Ports { get; init; } = [];
    public bool UsesBuild { get; init; }
}
