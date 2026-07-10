using SentryOperator.Docker.Compose;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace SentryOperator.Docker;

public class DependsOnTypeConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(Dictionary<string, DependsOn>);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        // The two scenarios here are:
        // 1. The underlying YAML is a list of strings, each of which we have to convert to DependsOn with default values
        // 2. The underlying YAML is a proper YAML structure of the dictionary, we just have to unroll it

        if (parser.TryConsume<MappingStart>(out _))
        {
            return ParseMapping(parser, rootDeserializer); // We're parsing a YAML object
        }

        if (parser.TryConsume<SequenceStart>(out _))
        {
            return ParseSequence(parser); // We're parsing a YAML array
        }
        
        throw new InvalidOperationException("Expected a YAML object or array");
    }

    private object? ParseMapping(IParser parser, ObjectDeserializer rootDeserializer)
    {
        var items = new Dictionary<string, DependsOn>();

        // Read all the mapping values until we reach the end of the YAML mapping
        while (!parser.Accept<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>();
            var dependsOn = rootDeserializer(typeof(DependsOn)) as DependsOn;
            items[key.Value] = dependsOn;
        }

        // Consume the mapping end token
        parser.MoveNext();
        return items;
    }

    private object? ParseSequence(IParser parser)
    {
        var items = new Dictionary<string, DependsOn>();
        
        // Read all the array values until we reach the end of the YAML array
        while (!parser.Accept<SequenceEnd>(out _))
        {
            var scalar = parser.Consume<Scalar>();

            items[scalar.Value] = new DependsOn(); // Create a new DependsOn with default values
        }

        // Consume the mapping end token
        parser.MoveNext();
        return items;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        serializer(value, type);
    }
}