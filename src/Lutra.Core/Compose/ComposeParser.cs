using YamlDotNet.RepresentationModel;

namespace Lutra.Core.Compose;

public static class ComposeParser
{
    private static readonly string[] DefaultFileNames =
        ["docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml"];

    public static string? FindComposeFile(string directory)
    {
        foreach (var name in DefaultFileNames)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    public static ComposeFile Parse(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Compose file not found: {filePath}");

        var yaml = File.ReadAllText(filePath);
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));

        if (stream.Documents.Count == 0)
            throw new InvalidOperationException("Compose file is empty.");

        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var services = new List<ComposeService>();

        if (!TryGetMapping(root, "services", out var servicesNode))
            throw new InvalidOperationException("No 'services' section found in compose file.");

        foreach (var (keyNode, valueNode) in servicesNode.Children)
        {
            var serviceName = ((YamlScalarNode)keyNode).Value!;
            if (valueNode is not YamlMappingNode serviceMapping)
                continue;

            services.Add(ParseService(serviceName, serviceMapping));
        }

        return new ComposeFile { Services = services };
    }

    private static ComposeService ParseService(string name, YamlMappingNode node)
    {
        string? image = null;
        string? containerName = null;
        var environment = new Dictionary<string, string>();
        var ports = new List<string>();
        var usesBuild = false;

        if (TryGetScalar(node, "image", out var imageValue))
            image = imageValue;

        if (TryGetScalar(node, "container_name", out var containerValue))
            containerName = containerValue;

        if (node.Children.TryGetValue(new YamlScalarNode("build"), out _))
            usesBuild = true;

        if (node.Children.TryGetValue(new YamlScalarNode("environment"), out var envNode))
            environment = ParseEnvironment(envNode);

        if (node.Children.TryGetValue(new YamlScalarNode("ports"), out var portsNode) &&
            portsNode is YamlSequenceNode portsSeq)
        {
            foreach (var portNode in portsSeq.Children.OfType<YamlScalarNode>())
            {
                if (portNode.Value is not null)
                    ports.Add(portNode.Value);
            }
        }

        return new ComposeService
        {
            ServiceName = name,
            Image = image,
            ContainerName = containerName,
            Environment = environment,
            Ports = ports,
            UsesBuild = usesBuild
        };
    }

    private static Dictionary<string, string> ParseEnvironment(YamlNode node)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (node is YamlMappingNode mappingNode)
        {
            foreach (var (keyNode, valueNode) in mappingNode.Children)
            {
                var key = ((YamlScalarNode)keyNode).Value;
                var value = valueNode is YamlScalarNode scalar ? scalar.Value ?? "" : "";
                if (key is not null)
                    env[key] = StripVariableSubstitution(value);
            }
        }
        else if (node is YamlSequenceNode sequenceNode)
        {
            foreach (var item in sequenceNode.Children.OfType<YamlScalarNode>())
            {
                if (item.Value is null)
                    continue;

                var eqIndex = item.Value.IndexOf('=');
                if (eqIndex > 0)
                    env[item.Value[..eqIndex]] = StripVariableSubstitution(item.Value[(eqIndex + 1)..]);
                else
                    env[item.Value] = "";
            }
        }

        return env;
    }

    private static string StripVariableSubstitution(string value)
    {
        if (!value.StartsWith("${"))
            return value;

        var inner = value[2..];
        if (inner.EndsWith('}'))
            inner = inner[..^1];

        var colonIndex = inner.IndexOf(":-", StringComparison.Ordinal);
        if (colonIndex >= 0)
            return inner[(colonIndex + 2)..];

        var dashIndex = inner.IndexOf('-');
        if (dashIndex >= 0)
            return inner[(dashIndex + 1)..];

        return value;
    }

    private static bool TryGetMapping(YamlMappingNode node, string key, out YamlMappingNode result)
    {
        if (node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlMappingNode mapping)
        {
            result = mapping;
            return true;
        }

        result = null!;
        return false;
    }

    private static bool TryGetScalar(YamlMappingNode node, string key, out string result)
    {
        if (node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar &&
            scalar.Value is not null)
        {
            result = scalar.Value;
            return true;
        }

        result = null!;
        return false;
    }
}
