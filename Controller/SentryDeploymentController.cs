using System.Security.Cryptography;
using System.Text;
using k8s;
using k8s.Models;
using KubeOps.KubernetesClient;
using KubeOps.Operator.Controller;
using KubeOps.Operator.Controller.Results;
using KubeOps.Operator.Entities.Extensions;
using KubeOps.Operator.Events;
using KubeOps.Operator.Finalizer;
using KubeOps.Operator.Rbac;
using Microsoft.Extensions.Caching.Distributed;
using SentryOperator.Docker;
using SentryOperator.Entities;
using SentryOperator.Extensions;
using SentryOperator.Finalizer;

namespace SentryOperator.Controller;

[EntityRbac(typeof(SentryDeployment), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Deployment), Verbs = RbacVerb.Create | RbacVerb.Delete | RbacVerb.Patch | RbacVerb.Update | RbacVerb.Get | RbacVerb.List)]
[EntityRbac(typeof(V1Secret), Verbs = RbacVerb.Create | RbacVerb.Delete | RbacVerb.Patch | RbacVerb.Update | RbacVerb.Get | RbacVerb.List)]
[EntityRbac(typeof(V1ConfigMap), Verbs = RbacVerb.Create | RbacVerb.Delete | RbacVerb.Patch | RbacVerb.Update | RbacVerb.Get | RbacVerb.List)]
[GenericRbac(Resources = new[]{"certificates"}, Groups = new[]{"cert-manager.io"}, Verbs = RbacVerb.Get | RbacVerb.Delete | RbacVerb.Patch | RbacVerb.Create)]
public class SentryDeploymentController : IResourceController<SentryDeployment>
{
    private const string DockerComposeUrl = "https://raw.githubusercontent.com/getsentry/self-hosted/master/docker-compose.yml";
    private readonly ILogger<SentryDeploymentController> _logger;
    private readonly IFinalizerManager<SentryDeployment> _finalizerManager;
    private readonly HttpClient _httpClient;
    private readonly IKubernetesClient _client;
    private readonly IDistributedCache _cache;
    private readonly IEventManager _manager;
    
    public SentryDeploymentController(ILogger<SentryDeploymentController> logger, IFinalizerManager<SentryDeployment> finalizerManager, IHttpClientFactory httpClientFactory, IKubernetesClient client, IDistributedCache cache, IEventManager manager)
    {
        _logger = logger;
        _finalizerManager = finalizerManager;
        _client = client;
        _cache = cache;
        _manager = manager;
        _httpClient = httpClientFactory.CreateClient();
    }

    public async Task<ResourceControllerResult?> ReconcileAsync(SentryDeployment entity)
    {
        entity.Status.Status = "Updating";
        await _client.UpdateStatus(entity);
        _logger.LogInformation("Entity {Name} called {ReconcileAsyncName}", entity.Name(), nameof(ReconcileAsync));
        await _finalizerManager.RegisterFinalizerAsync<SentryDeploymentFinalizer>(entity);

        var (deployments, services) = await FetchAndConvertDockerCompose(entity);

        var secret = await _client.Get<V1Secret>("sentry-env", entity.Namespace());

        var config = entity.Spec.Config ?? new();
        if (secret == null)
        {
            var secretKeyBytes = RandomNumberGenerator.GetBytes(64);
            var secretKey = Convert.ToBase64String(secretKeyBytes);
            
            await _client.Create(new V1Secret()
            {
                Metadata = new V1ObjectMeta
                {
                    Name = "sentry-env",
                    NamespaceProperty = entity.Namespace()
                },
                StringData = new Dictionary<string, string>
                {
                    ["SENTRY_EVENT_RETENTION_DAYS"] = config.EventRetentionDays.ToString(),
                    ["SENTRY_SECRET_KEY"] = secretKey,
                    ["SENTRY_VSTS_CLIENT_ID"] = "",
                    ["SENTRY_VSTS_CLIENT_SECRET"] = "",
                    ["SNUBA"] = "http://snuba-api:1218"
                }
            });
        }
        
        // var snubaEnvConfigMap = await _client.Get<V1ConfigMap>("snuba-env", entity.Namespace());
        //
        // if (snubaEnvConfigMap == null)
        // {
        //     await _client.Create(new V1ConfigMap()
        //     {
        //         Metadata = new V1ObjectMeta
        //         {
        //             Name = "snuba-env",
        //             NamespaceProperty = entity.Namespace()
        //         },
        //         Data = new Dictionary<string, string>
        //         {
        //             ["CLICKHOUSE_HOST"] = "clickhouse",
        //             ["CLICKHOUSE_PORT"] = "9000",
        //             ["DEFAULT_BROKERS"] = "kafka-service:9092",
        //             ["REDIS_HOST"] = "redis",
        //             ["REDIS_PORT"] = "6379",
        //             ["SNUBA_SETTINGS"] = "docker",
        //             ["UWSGI_DIE_ON_TERM"] = "true",
        //             ["UWSGI_DISABLE_LOGGING"] = "true",
        //             ["UWSGI_DISABLE_WRITE_EXCEPTION"] = "true",
        //             ["UWSGI_ENABLE_THREADS"] = "true",
        //             ["UWSGI_IGNORE_SIGPIPE"] = "true",
        //             ["UWSGI_IGNORE_WRITE_ERRORS"] = "true",
        //             ["UWSGI_MAX_REQUESTS"] = "10000",
        //             ["UWSGI_NEED_APP"] = "true"
        //             
        //         }
        //     });
        // }

        foreach (var service in services)
        {
            service.AddOwnerReference(entity.MakeOwnerReference());
            var checksum = service.GetChecksum(); 
            service.SetLabel("sentry-operator/checksum", checksum);
            var svc = await _client.Get<V1Service>(service.Name(), service.Namespace());
            if (svc == null)
            {
                await _client.Create(service);
            }
            else if(svc.GetLabel("app.kubernetes.io/managed-by") == "sentry-operator")
            {
                if (svc.GetLabel("sentry-operator/checksum") != checksum)
                {
                    service.Metadata.ResourceVersion = svc.Metadata.ResourceVersion;
                    await _client.Update(service);
                }
            }
        }

        var actualDeployments = await _client.List<V1Deployment>(entity.Namespace(), labelSelector: $"app.kubernetes.io/managed-by=sentry-operator");
        
        foreach (var deployment in deployments)
        {
            deployment.AddOwnerReference(entity.MakeOwnerReference());
            var checksum = deployment.GetChecksum(); 
            deployment.SetLabel("sentry-operator/checksum", checksum);
            var actualDeployment = actualDeployments.FirstOrDefault(d => d.Name() == deployment.Name());
            if (actualDeployment == null)
            {
                await _client.Create(deployment);
            }
            else if(actualDeployment.GetLabel("app.kubernetes.io/managed-by") == "sentry-operator")
            {
                if (actualDeployment.GetLabel("sentry-operator/checksum") != checksum || entity.Spec.Version == "nightly")
                {
                    deployment.Metadata.ResourceVersion = actualDeployment.Metadata.ResourceVersion;
                    await _client.Update(deployment);
                }
            }
        }
        
        foreach(var deployment in actualDeployments)
        {
            if (deployments.All(d => d.Name() != deployment.Name()))
            {
                await _client.Delete(deployment);
            }
        }

        if (!(entity.Spec.Certificate?.Install ?? true))
        {
            entity.Status.Status = "Ready";
            await _client.UpdateStatus(entity);
            return null;
        }
        
        var certName = entity.Spec.Certificate?.CertificateCRDName ?? (entity.Name() + "-certificate");
        var certificate = await _client.Get<Certificate>(certName, entity.Namespace());
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
                await _client.Create(certificate);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error creating certificate");
                
                entity.Status.Status = "Error";
                await _client.UpdateStatus(entity);
                await _manager.PublishAsync(entity, "Error", "Error creating certificate: " + e.Message);
                return ResourceControllerResult.RequeueEvent(TimeSpan.FromSeconds(15));
            }
        }

        entity.Status.Status = "Ready";
        await _client.UpdateStatus(entity);
        //return ResourceControllerResult.RequeueEvent(TimeSpan.FromSeconds(15));
        return null;
    }

    public Task StatusModifiedAsync(SentryDeployment entity)
    {
        _logger.LogInformation("Entity {Name} called {StatusModifiedAsyncName}", entity.Name(), nameof(StatusModifiedAsync));

        return Task.CompletedTask;
    }

    public async Task DeletedAsync(SentryDeployment entity)
    {
        _logger.LogInformation("Entity {Name} called {DeletedAsyncName}", entity.Name(), nameof(DeletedAsync));

        var deployments = await _client.List<V1Deployment>(entity.Namespace(), labelSelector: $"app.kubernetes.io/managed-by=sentry-operator");
        foreach (var deployment in deployments)
        {
            await _client.Delete(deployment);
        }
        
        var services = await _client.List<V1Service>(entity.Namespace(), labelSelector: $"app.kubernetes.io/managed-by=sentry-operator");
        foreach (var service in services)
        {
            await _client.Delete(service);
        }
        
        var configMaps = await _client.List<V1ConfigMap>(entity.Namespace(), labelSelector: $"app.kubernetes.io/managed-by=sentry-operator");
        foreach (var configMap in configMaps)
        {
            await _client.Delete(configMap);
        }
        
        var secrets = await _client.List<V1Secret>(entity.Namespace(), labelSelector: $"app.kubernetes.io/managed-by=sentry-operator");
        foreach (var secret in secrets)
        {
            await _client.Delete(secret);
        }
        
        var certName = entity.Name() + "-certificate";
        var certificate = await _client.Get<Certificate>(certName, entity.Namespace());
        if (certificate != null)
        {
            await _client.Delete(certificate);
        }
    }

    private async Task<(V1Deployment[] Deployments, V1Service[] Services)> FetchAndConvertDockerCompose(SentryDeployment entity)
    {
        var dockerComposeUrl = DockerComposeUrl; 
        if (entity.Spec.DockerComposeUrl != null)
        {
            dockerComposeUrl = entity.Spec.DockerComposeUrl;
        }
        else if (entity.Spec.Version != null)
        {
            dockerComposeUrl = $"https://raw.githubusercontent.com/getsentry/self-hosted/{(entity.Spec.Version == "nightly" ? "master" : entity.Spec.Version)}/docker-compose.yml";
        }

        var dockerComposeRaw = await _cache.GetStringAsync(dockerComposeUrl);
        if (dockerComposeRaw == null)
        {
            dockerComposeRaw = await _httpClient.GetStringAsync(dockerComposeUrl);
            if (!string.IsNullOrWhiteSpace(dockerComposeRaw))
            {
                await _cache.SetStringAsync(dockerComposeUrl, dockerComposeRaw, new DistributedCacheEntryOptions()
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24)
                });
            }
        }
        dockerComposeRaw = (entity.Spec.Config ?? new()).ReplaceVariables(dockerComposeRaw);
        var dockerComposeConverter = new DockerComposeConverter();
        var (deployments, services) = dockerComposeConverter.Convert(dockerComposeRaw, entity, entity.Spec.Version == "master" ? "nightly" : entity.Spec.Version ?? "nightly");
        
        return (deployments, services);
    }
}

