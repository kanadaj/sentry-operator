using YamlDotNet.Serialization;

namespace SentryOperator.Docker.Compose;

public class Ulimits
{
    [YamlMember(Alias = "nofile")]
    public Nofile? Nofile { get; set; }
}