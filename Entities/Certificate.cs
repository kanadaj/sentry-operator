using k8s.Models;
using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace SentryOperator.Entities;

[Ignore]
[KubernetesEntity(Group = "cert-manager.io", ApiVersion = "v1", Kind = "Certificate", PluralName = "certificates")]
public class Certificate : CustomKubernetesEntity<Certificate.CertificateSpec, Certificate.CertificateStatus>
{
    public Certificate()
    {
        ApiVersion = "cert-manager.io/v1";
        Kind = "Certificate";
    }
    
    public class CertificateSpec
    {
        public string SecretName { get; set; } = string.Empty;
        
        /// <summary>
        /// Duration in hours with a h suffix
        /// </summary>
        public string Duration { get; set; } = "8760h";
        public string? CommonName { get; set; }
        public List<string>? DnsNames { get; set; }
        public List<string>? IpAddresses { get; set; }
        public IssuerReference? IssuerRef { get; set; }
    }
    
    public class IssuerReference
    {
        public string Name { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string? Group { get; set; }
    }
    
    public class CertificateStatus
    {
        public IList<V1ComponentCondition>? Conditions { get; set; }
        public DateTime? NotBefore { get; set; }
        public DateTime? NotAfter { get; set; }
        public DateTime? RenewalTime { get; set; }
        public int? Revision { get; set; }
        
    }
}
