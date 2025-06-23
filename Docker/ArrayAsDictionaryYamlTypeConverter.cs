using System.Collections;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace SentryOperator.Docker;

public class ArrayAsDictionaryNodeDeserializer : INodeDeserializer
{
    private readonly INodeDeserializer _dictionaryDeserializer;

    public ArrayAsDictionaryNodeDeserializer(INodeDeserializer dictionaryNodeDeserializer)
    {
        _dictionaryDeserializer = dictionaryNodeDeserializer;
    }

    public bool Deserialize(IParser reader, Type expectedType, Func<IParser, Type, object?> nestedObjectDeserializer, out object? value, ObjectDeserializer ser)
    {
        if (typeof(IDictionary).IsAssignableFrom(expectedType) || typeof(IDictionary<,>).IsAssignableFrom(expectedType))
        {
            if (reader.Current is MappingStart)
            {
                return _dictionaryDeserializer.Deserialize(reader, expectedType, nestedObjectDeserializer, out value, ser);
            }
            else if (reader.Current is SequenceStart)
            {
                var stringList = new List<string>();
                reader.MoveNext();
                while (reader.Current is not SequenceEnd)
                {
                    if (reader.Current is YamlDotNet.Core.Events.Scalar scalar)
                    {
                        stringList.Add(scalar.Value);
                    }
                    else
                    {
                        throw new YamlException(reader.Current.Start, reader.Current.End, "Expected a scalar value in the sequence.");
                    }
                    reader.MoveNext();
                }
                reader.MoveNext(); // Move past the SequenceEnd
                // Convert the list to a dictionary with indices as keys
                var dictionary = new Dictionary<string, string>(stringList.Count);
                for (int i = 0; i < stringList.Count; i++)
                {
                    // Parse items as key-value pairs
                    var keyValue = stringList[i].Split(new[] { '=' }, 2);
                    if (keyValue.Length == 2)
                    {
                        dictionary[keyValue[0].Trim()] = keyValue[1].Trim();
                    }
                    else
                    {
                        throw new YamlException(reader.Current.Start, reader.Current.End, "Expected key=value format in the sequence.");
                    }
                }
                value = dictionary;
                return true;
            }
        }

        value = null;
        return false;
    }
}