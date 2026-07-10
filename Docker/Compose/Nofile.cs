using YamlDotNet.Serialization;

namespace SentryOperator.Docker.Compose;

public class Nofile
{
    [YamlMember(Alias = "soft")]
    public int? Soft { get; set; }
    
    [YamlMember(Alias = "hard")]
    public int? Hard { get; set; }
}