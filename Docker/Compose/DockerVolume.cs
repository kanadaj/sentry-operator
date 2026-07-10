using YamlDotNet.Serialization;

namespace SentryOperator.Docker.Compose;

public class DockerVolume
{
    [YamlMember(Alias = "external")]
    public bool External { get; set; }
}