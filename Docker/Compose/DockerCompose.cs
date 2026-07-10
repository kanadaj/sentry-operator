using YamlDotNet.Serialization;

namespace SentryOperator.Docker.Compose;

public class DockerCompose
{
    [YamlMember(Alias = "x-restart-policy")]
    public DockerService RestartPolicy { get; set; }
    
    [YamlMember(Alias = "x-pull-policy")]
    public PullPolicy? PullPolicy { get; set; }

    [YamlMember(Alias = "x-depends_on-healthy")]
    public DependsOn DependsOnHealthy { get; set; }

    [YamlMember(Alias = "x-depends_on-default")]
    public DependsOn DependsOnDefault { get; set; }

    [YamlMember(Alias = "x-healthcheck-defaults")]
    public Healthcheck HealthcheckDefaults { get; set; }
    
    [YamlMember(Alias = "x-file-healthcheck")]
    public Healthcheck FileHealthcheck { get; set; }

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