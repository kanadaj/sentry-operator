using System.ComponentModel;
using k8s.Models;
using KubeOps.Abstractions.Entities;

namespace SentryOperator.Entities;

[KubernetesEntity(Group = "sentry.io", ApiVersion = "v1", Kind = "SentryDeployment", PluralName = "sentrydeployments")]
public class SentryDeployment : CustomKubernetesEntity<SentryDeployment.SentryDeploymentSpec, SentryDeployment.SentryDeploymentStatus>
{
    public class SentryDeploymentSpec
    {
        [Description("The version of sentry to use; defaults to 23.6.1. Make sure to either use a version tag or 'nightly'")]
        public string? Version { get; set; } = "23.6.1";
        
        [Description("The URL of the docker-compose.yml file; if specified we will use the version for the images but the docker-compose for the container architecture")]
        public string? DockerComposeUrl { get; set; }
        
        [Description("Custom YAML that will be merged into the docker-compose.yml file; this is useful for overriding the default configuration")]
        public string? DockerComposeOverrides { get; set; }
        
        [Description("The config for the Sentry deployment")]
        public SentryDeploymentConfig? Config { get; set; }
        
        [Description("Override each container's number of replicas here by name")]
        public Dictionary<string, int>? Replicas { get; set; }
        
        [Description("Override environmental variables here; this is applied to all containers with matching env vars")]
        public Dictionary<string, string>? Environment { get; set; }
        
        [Description("Override the default resource limits")]
        public ResourceLimitConfig? Resources { get; set; }
        
        public SentryDeploymentCertificateConfig? Certificate { get; set; } 
        
        public string GetVersion()
        {
            return Version ?? "nightly";
        }
    }

    public class SentryDeploymentStatus
    {
        public string Status { get; set; } = string.Empty;
        public string? Message { get; set; }
        
        public string? LastVersion { get; set; }
    }
}

public class SentryDeploymentCertificateConfig
{
    public bool? Install { get; set; }
    
    [Description("Override the name of the Certificate CRD generated")]
    public string? CertificateCRDName { get; set; }
    
    [Description("Override the name of the issuer")]
    public string? IssuerName { get; set; }
    
    [Description("Override the kind of the issuer")]
    public string IssuerKind { get; set; } = "ClusterIssuer";
    
    [Description("Override the name of the certificate secret")]
    public string? SecretName { get; set; }
    
    [Description("Add additional hosts to the certificate")]
    public string[] CustomHosts { get; set; } = Array.Empty<string>();
}

public class ResourceLimitConfig : Dictionary<string, ResourceRequirementDefinition>
{
    public ResourceRequirementDefinition? Web { get; set; }
    public ResourceRequirementDefinition? Worker { get; set; }
    public ResourceRequirementDefinition? Cron { get; set; }
    public ResourceRequirementDefinition? Snuba { get; set; }
    public ResourceRequirementDefinition? Relay { get; set; }
    public ResourceRequirementDefinition? Consumer { get; set; }
    public ResourceRequirementDefinition? Ingest { get; set; }
    public ResourceRequirementDefinition? Forwarder { get; set; }
    public ResourceRequirementDefinition? Replacer { get; set; }
    // ReSharper disable once InconsistentNaming
    public ResourceRequirementDefinition? GeoIP { get; set; }
}

public class ResourceRequirementDefinition
{
    public ResourceRequirement? Limits { get; set; }
    public ResourceRequirement? Requests { get; set; }
}

public class ResourceRequirement
{
    public string? Cpu { get; set; }
    public string? Memory { get; set; }
    
    public ResourceRequirement(string? cpu, string? memory)
    {
        Cpu = cpu;
        Memory = memory;
    }
    
    public ResourceRequirement() {}
    
    public static implicit operator Dictionary<string, ResourceQuantity>(ResourceRequirement requirement)
    {
        return requirement.ToDictionary();
    }

    public Dictionary<string, ResourceQuantity> ToDictionary()
    {
        var result = new Dictionary<string, ResourceQuantity>();
        if (Cpu != null)
        {
            result["cpu"] = new ResourceQuantity(Cpu);
        }
        if (Memory != null)
        {
            result["memory"] = new ResourceQuantity(Memory);
        }
        return result;
    }
    
    public override string ToString()
    {
        return $"Cpu: {Cpu}, Memory: {Memory}";
    }
}