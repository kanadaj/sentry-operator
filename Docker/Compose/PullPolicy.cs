using YamlDotNet.Serialization;

namespace SentryOperator.Docker.Compose;

public class PullPolicy
{
    [YamlMember(Alias = "pull_policy")]
    public string Policy { get; set; }
}