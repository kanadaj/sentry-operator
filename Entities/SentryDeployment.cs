using k8s.Models;
using KubeOps.Operator.Entities;
using KubeOps.Operator.Entities.Annotations;

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

public class ResourceLimitConfig : Dictionary<string, V1ResourceRequirements>
{
    public V1ResourceRequirements? Web { get; set; }
    public V1ResourceRequirements? Worker { get; set; }
    public V1ResourceRequirements? Cron { get; set; }
    public V1ResourceRequirements? Snuba { get; set; }
    public V1ResourceRequirements? Relay { get; set; }
    public V1ResourceRequirements? Consumer { get; set; }
    public V1ResourceRequirements? Ingest { get; set; }
    public V1ResourceRequirements? Forwarder { get; set; }
    public V1ResourceRequirements? Replacer { get; set; }
    // ReSharper disable once InconsistentNaming
    public V1ResourceRequirements? GeoIP { get; set; }
}