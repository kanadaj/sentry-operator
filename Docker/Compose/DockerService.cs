using YamlDotNet.Serialization;

namespace SentryOperator.Docker.Compose;

public class DockerService
{
    // [YamlMember(Alias = "<<")]
    // public string? InheritFrom { get; set; }
    
    [YamlMember(Alias = "image")]
    public string? Image { get; set; }
    
    [YamlMember(Alias = "platform")]
    public string? Platform { get; set; }
    
    [YamlMember(Alias = "restart")]
    public string? Restart { get; set; }
    
    [YamlMember(Alias = "depends_on")]
    public Dictionary<string, DependsOn>? DependsOn { get; set; }
    
    [YamlMember(Alias = "command")]
    public object? Command { get; set; }
    
    [YamlMember(Alias = "hostname")]
    public object? Hostname { get; set; }
    
    [YamlMember(Alias = "entrypoint")]
    public object? EntryPoint { get; set; }
    
    [YamlMember(Alias = "environment")]
    public Dictionary<string, string>? Environment { get; set; }
    
    [YamlMember(Alias = "volumes")]
    public List<object>? Volumes { get; set; }
    
    [YamlMember(Alias = "build")]
    public Build? Build { get; set; }
    
    [YamlMember(Alias = "ulimits")]
    public Ulimits? Ulimits { get; set; }
    
    [YamlMember(Alias = "healthcheck")]
    public Healthcheck? Healthcheck { get; set; }
    
    [YamlMember(Alias = "ports")]
    public List<string>? Ports { get; set; }
    
    [YamlMember(Alias = "profiles")]
    public List<string>? Profiles { get; set; }
}