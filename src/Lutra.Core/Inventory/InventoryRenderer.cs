using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lutra.Core.Inventory;

public static class InventoryRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static string ToJson(InventorySnapshot snapshot)
        => JsonSerializer.Serialize(Normalize(snapshot), JsonOptions) + "\n";

    private static InventorySnapshot Normalize(InventorySnapshot snapshot)
        => new()
        {
            CapturedAt = snapshot.CapturedAt,
            Host = snapshot.Host,
            LutraVersion = snapshot.LutraVersion,
            Sections = snapshot.Sections
                .OrderBy(section => section.Name, StringComparer.Ordinal)
                .Select(section => new InventorySection
                {
                    Name = section.Name,
                    Status = section.Status,
                    Required = section.Required,
                    ExitCode = section.ExitCode,
                    ErrorCategory = section.ErrorCategory,
                    Entries = section.Entries
                        .OrderBy(entry => entry.Kind, StringComparer.Ordinal)
                        .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                        .Select(entry => new InventoryEntry
                        {
                            Kind = entry.Kind,
                            Name = entry.Name,
                            Attributes = new SortedDictionary<string, string>(
                                entry.Attributes, StringComparer.Ordinal)
                        })
                        .ToList()
                })
                .ToList()
        };

    public static string ToMarkdown(InventorySnapshot snapshot)
    {
        var output = new StringBuilder();
        output.AppendLine("# Lutra Server Inventory");
        output.AppendLine();
        output.AppendLine($"- Captured (UTC): `{snapshot.CapturedAt:O}`");
        output.AppendLine($"- Host: `{snapshot.Host}`");
        output.AppendLine($"- Lutra version: `{snapshot.LutraVersion}`");
        output.AppendLine();
        output.AppendLine("> Secret values and arbitrary command output are intentionally omitted.");

        foreach (var section in snapshot.Sections.OrderBy(section => section.Name, StringComparer.Ordinal))
        {
            output.AppendLine();
            output.AppendLine($"## {section.Name}");
            output.AppendLine();
            output.AppendLine($"Status: `{section.Status.ToString().ToLowerInvariant()}`"
                              + (section.Required ? " (required)" : ""));
            if (section.ExitCode is not null)
                output.AppendLine($"Exit code: `{section.ExitCode}`");
            if (section.ErrorCategory is not null)
                output.AppendLine($"Error: `{section.ErrorCategory}`");

            foreach (var entry in section.Entries.OrderBy(entry => entry.Kind).ThenBy(entry => entry.Name))
            {
                output.AppendLine();
                output.AppendLine($"### {entry.Kind}: {entry.Name}");
                foreach (var attribute in entry.Attributes)
                    output.AppendLine($"- {attribute.Key}: `{attribute.Value}`");
            }

            if (section.Entries.Count == 0)
                output.AppendLine("\n_No entries._");
        }

        return output.ToString();
    }
}
