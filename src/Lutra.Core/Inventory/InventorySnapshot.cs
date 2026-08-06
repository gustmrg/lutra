using System.Text.Json.Serialization;

namespace Lutra.Core.Inventory;

public sealed class InventorySnapshot
{
    public required DateTime CapturedAt { get; init; }
    public required string Host { get; init; }
    public required string LutraVersion { get; init; }
    public required List<InventorySection> Sections { get; init; }

    [JsonIgnore]
    public bool HasRequiredFailures => Sections.Any(section =>
        section.Required && section.Status != InventoryCollectorStatus.Succeeded);
}

public sealed class InventorySection
{
    public required string Name { get; init; }
    public required InventoryCollectorStatus Status { get; init; }
    public required bool Required { get; init; }
    public int? ExitCode { get; init; }
    public string? ErrorCategory { get; init; }
    public List<InventoryEntry> Entries { get; init; } = [];
}

public sealed class InventoryEntry
{
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public SortedDictionary<string, string> Attributes { get; init; } = new(StringComparer.Ordinal);
}

public enum InventoryCollectorStatus
{
    Succeeded,
    Failed,
    NotApplicable
}

public sealed record InventoryCollectionPolicy(
    bool RequirePackages = false,
    bool RequireDocker = false,
    bool RequireSystemd = false,
    IReadOnlyList<string>? OptionalCollectors = null,
    bool IncludePackages = true,
    bool IncludeDocker = true,
    bool IncludeSystemd = true);
