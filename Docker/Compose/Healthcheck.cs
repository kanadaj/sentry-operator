using YamlDotNet.Serialization;

namespace SentryOperator.Docker.Compose;

public class Healthcheck
{
    [YamlMember(Alias = "interval")]
    public string? Interval { get; set; }
    
    [YamlMember(Alias = "timeout")]
    public string? Timeout { get; set; }
    
    [YamlMember(Alias = "retries")]
    public string? Retries { get; set; }
    [YamlMember(Alias = "start_period")] 
    public string? StartPeriod { get; set; }
    
    [YamlMember(Alias = "test")]
    public object? Test { get; set; }
}