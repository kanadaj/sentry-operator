using SentryOperator.Docker.Compose;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NodeDeserializers;

namespace SentryOperator.Docker;

public class YamlDockerComposeParser : IDockerComposeParser
{
    public DockerCompose Parse(string dockerComposeYaml, string? overrides)
    {
        var deserializer = new DeserializerBuilder()
            .WithNodeDeserializer(
                inner => new ArrayAsDictionaryNodeDeserializer(inner),
                syntax => syntax.InsteadOf<DictionaryNodeDeserializer>())
            .WithTypeConverter(new DependsOnTypeConverter())
            .IgnoreUnmatchedProperties()
            .Build();

        var dockerCompose = Deserialize(dockerComposeYaml, deserializer);
        if (string.IsNullOrWhiteSpace(overrides))
        {
            return dockerCompose;
        }

        var overrideCompose = Deserialize($"{dockerComposeYaml}\n{overrides}", deserializer);
        if (overrideCompose.Services == null)
        {
            return dockerCompose;
        }

        dockerCompose.Services ??= new Dictionary<string, DockerService>();
        foreach (var service in overrideCompose.Services)
        {
            dockerCompose.Services[service.Key] = service.Value;
        }

        return dockerCompose;
    }

    private static DockerCompose Deserialize(string yaml, IDeserializer deserializer)
    {
        var parser = new MergingParser(new Parser(new StringReader(yaml)));
        return deserializer.Deserialize<DockerCompose>(parser);
    }
}
