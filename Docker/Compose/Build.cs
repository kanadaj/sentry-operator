using YamlDotNet.Serialization;

namespace SentryOperator.Docker.Compose;

public class Build
{
    [YamlMember(Alias = "context")]
    public string? Context { get; set; }
    
    [YamlMember(Alias = "args")]
    public object? Args { get; set; }
}