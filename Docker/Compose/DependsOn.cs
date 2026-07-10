using YamlDotNet.Serialization;

namespace SentryOperator.Docker.Compose;

public class DependsOn
{
    [YamlMember(Alias = "condition")]
    public ServiceCondition Condition { get; set; }
    
    [YamlMember(Alias = "restart")]
    public bool? Restart { get; set; }
}