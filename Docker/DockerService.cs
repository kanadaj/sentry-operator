using System.Collections;
using System.Reflection;
using SentryOperator.Extensions;
using YamlDotNet.Serialization;

namespace SentryOperator.Docker;

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
    public object? DependsOn { get; set; }
    
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

public class DependsOn
{
    [YamlMember(Alias = "condition")]
    public string? Condition { get; set; }
}

public class Build
{
    [YamlMember(Alias = "context")]
    public string? Context { get; set; }
    
    [YamlMember(Alias = "args")]
    public object? Args { get; set; }
}

public class Ulimits
{
    [YamlMember(Alias = "nofile")]
    public Nofile? Nofile { get; set; }
}

public class Nofile
{
    [YamlMember(Alias = "soft")]
    public int? Soft { get; set; }
    
    [YamlMember(Alias = "hard")]
    public int? Hard { get; set; }
}

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

public class DockerCompose
{
    [YamlMember(Alias = "x-restart-policy")]
    public DockerService RestartPolicy { get; set; }
    
    [YamlMember(Alias = "x-pill-policy")]
    public PullPolicy? PullPolicy { get; set; }

    [YamlMember(Alias = "x-depends_on-healthy")]
    public DependsOn DependsOnHealthy { get; set; }

    [YamlMember(Alias = "x-depends_on-default")]
    public DependsOn DependsOnDefault { get; set; }

    [YamlMember(Alias = "x-healthcheck-defaults")]
    public Healthcheck HealthcheckDefaults { get; set; }

    [YamlMember(Alias = "x-sentry-defaults")]
    public DockerService SentryDefaults { get; set; }

    [YamlMember(Alias = "x-snuba-defaults")]
    public DockerService SnubaDefaults { get; set; }
    
    [YamlMember(Alias = "services")] 
    public Dictionary<string, DockerService>? Services { get; set; }
    
    [YamlMember(Alias = "volumes")] 
    public Dictionary<string, DockerVolume>? Volumes { get; set; }
    //public XProperties? XProperties { get; set; }
}

public class PullPolicy
{
    [YamlMember(Alias = "pull_policy")]
    public bool Policy { get; set; }
}

public class DockerVolume
{
    [YamlMember(Alias = "external")]
    public bool External { get; set; }
}