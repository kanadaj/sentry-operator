using k8s;
using k8s.Models;
using KubeOps.Abstractions.Events;
using KubeOps.Abstractions.Entities;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using SentryOperator.Entities;
using SentryOperator.Extensions;

namespace SentryOperator.Services;

public class CertificateProvisioner : ICertificateProvisioner
{
    private readonly IKubernetesClient _client;
    private readonly EventPublisher _eventPublisher;
    private readonly ILogger<CertificateProvisioner> _logger;

    public CertificateProvisioner(IKubernetesClient client, EventPublisher eventPublisher, ILogger<CertificateProvisioner> logger)
    {
        _client = client;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    public async Task<bool> EnsureAsync(SentryDeployment entity)
{
    var certName = entity.Spec.Certificate?.CertificateCRDName ?? (entity.Name() + "-certificate");
    var certificate = await _client.GetAsync<Certificate>(certName, entity.Namespace());
    _logger.LogInformation("Certificate {CertificateName} found: {CertificateFound}", certName, certificate != null);
    if (certificate == null)
    {
        certificate = new Certificate()
        {
            Metadata = new V1ObjectMeta
            {
                Name = certName,
                NamespaceProperty = entity.Namespace()
            },
            Spec = new Certificate.CertificateSpec()
            {
                CommonName = "sentry." + entity.Namespace() + ".svc.cluster.local",
                Duration = "87600h",
                DnsNames = new List<string>()
                {
                    entity.Name(),
                    entity.Name() + "." + entity.Namespace(),
                    entity.Name() + "." + entity.Namespace() + ".svc.cluster.local"
                }.Concat(entity.Spec.Certificate?.CustomHosts ?? Array.Empty<string>()).ToList(),
                IssuerRef = new Certificate.IssuerReference()
                {
                    Name = entity.Spec.Certificate?.IssuerName ?? "self-signed",
                    Kind = entity.Spec.Certificate?.IssuerKind ?? "ClusterIssuer"
                },
                SecretName = entity.Spec.Certificate?.SecretName ?? (entity.Name() + "-certificate")
            }
        };
        certificate.AddOwnerReference(entity.MakeOwnerReference());

        _logger.LogInformation("Creating certificate {CertificateName}: {Certificate}", certName, KubernetesYaml.Serialize(certificate));
        try
        {
            await _client.CreateAsync(certificate);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error creating certificate");

            entity.Status.Status = "Error";
            entity.Status.Message = "Error creating certificate: " + e.Message;
            await _client.UpdateStatusAsync(entity);
            await _eventPublisher(entity, "Error", "Error creating certificate: " + e.Message);
            return false;
        }
    }

    return true;
}

}
