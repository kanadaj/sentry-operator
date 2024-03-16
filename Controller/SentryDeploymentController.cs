using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
[EntityRbac(typeof(V1Service), Verbs = RbacVerb.Create | RbacVerb.Delete | RbacVerb.Patch | RbacVerb.Update | RbacVerb.Get | RbacVerb.List)]
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
    private readonly DockerComposeConverter _dockerComposeConverter;

    public SentryDeploymentController(ILogger<SentryDeploymentController> logger, IFinalizerManager<SentryDeployment> finalizerManager, IHttpClientFactory httpClientFactory, IKubernetesClient client, IDistributedCache cache, IEventManager manager, DockerComposeConverter dockerComposeConverter)
    {
        _logger = logger;
        _finalizerManager = finalizerManager;
        _client = client;
        _cache = cache;
        _manager = manager;
        _dockerComposeConverter = dockerComposeConverter;
        _httpClient = httpClientFactory.CreateClient();
    }

    public async Task<ResourceControllerResult?> ReconcileAsync(SentryDeployment entity)
    {
        _logger.LogInformation("Entity {Name} called {ReconcileAsyncName}", entity.Name(), nameof(ReconcileAsync));
        await _finalizerManager.RegisterFinalizerAsync<SentryDeploymentFinalizer>(entity);

        var resources = await FetchAndConvertDockerCompose(entity);

        await AddDefaultConfig(entity);
        
        // We don't need to query the resources if the status has the same checksum as the generated spec
        var configChecksum = resources.GetChecksum();
        if (entity.Status.LastVersion == configChecksum && configChecksum != null)
        {
            return null;
        }

        var services = resources.OfType<V1Service>().ToList();
        var deployments = resources.OfType<V1Deployment>().ToList();
        
        var actualServices = await _client.List<V1Service>(entity.Namespace());
        var actualDeployments = await _client.List<V1Deployment>(entity.Namespace());

        if (!CheckIfUpdateIsNeeded(services, actualServices, deployments, actualDeployments, entity))
        {
            if (entity.Status.Status != "Ready")
            {
                entity.Status.Status = "Ready";
                entity.Status.Message = "Sentry deployment is ready";
                entity.Status.LastVersion = configChecksum;
                await _client.UpdateStatus(entity);
            }
            return null;
        }
        
        entity.Status.Status = "Updating";
        entity.Status.Message = "Updating Sentry deployment";
        await _client.UpdateStatus(entity);
        
        foreach (var service in services)
        {
            service.AddOwnerReference(entity.MakeOwnerReference());
            var checksum = service.GetChecksum(); 
            service.SetLabel("sentry-operator/checksum", checksum);
            var svc = actualServices.FirstOrDefault(s => s.Name() == service.Name());
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

                    if (deployment.Metadata.Name == "snuba-api")
                    {
                        await InstallKafkaTopics(entity);
                    }
                }
            }
        }
        
        foreach(var deployment in actualDeployments)
        {
            if (deployments.All(d => d.Name() != deployment.Name()) && deployment.GetLabel("app.kubernetes.io/managed-by") == "sentry-operator")
            {
                await _client.Delete(deployment);
            }
        }

        if ((entity.Spec.Certificate?.Install ?? true))
        {
            var result = await AddCertManagerConfig(entity);
            if (result != null)
            {
                return result;
            }
        }

        entity = (await _client.Get<SentryDeployment>(entity.Name(), entity.Namespace()))!;
        entity.Status.Status = "Ready";
        entity.Status.Message = "Sentry deployment is ready";
        entity.Status.LastVersion = configChecksum;
        await _client.UpdateStatus(entity);
        //return ResourceControllerResult.RequeueEvent(TimeSpan.FromSeconds(15));
        return null;
    }

    private bool CheckIfUpdateIsNeeded(List<V1Service> services, IList<V1Service> actualServices, List<V1Deployment> deployments, IList<V1Deployment> actualDeployments, SentryDeployment entity)
    {
        foreach (var service in services)
        {
            var matchingService = actualServices.FirstOrDefault(s => s.Name() == service.Name());
            if (matchingService.GetLabel("app.kubernetes.io/managed-by") != "sentry-operator")
            {
                continue;
            }
            
            _logger.LogInformation("Service {ServiceName} checksum: {ServiceChecksum}, actual checksum: {ActualChecksum}", service.Name(), service.GetChecksum(), matchingService.GetLabel("sentry-operator/checksum"));
            
            if (matchingService.GetLabel("sentry-operator/checksum") != service.GetChecksum())
            {
                return true;
            }
        }
        
        foreach (var deployment in deployments)
        {
            var matchingDeployment = actualDeployments.FirstOrDefault(d => d.Name() == deployment.Name());
            if (matchingDeployment.GetLabel("app.kubernetes.io/managed-by") != "sentry-operator")
            {
                continue;
            }
            
            _logger.LogInformation("Deployment {DeploymentName} checksum: {DeploymentChecksum}, actual checksum: {ActualChecksum}", deployment.Name(), deployment.GetChecksum(), matchingDeployment.GetLabel("sentry-operator/checksum"));
            
            if (matchingDeployment.GetLabel("sentry-operator/checksum") != deployment.GetChecksum())
            {
                return true;
            }
        }
        
        return false;
    }

    private async Task<ResourceControllerResult?> AddCertManagerConfig(SentryDeployment entity)
    {
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
                entity.Status.Message = "Error creating certificate: " + e.Message;
                await _client.UpdateStatus(entity);
                await _manager.PublishAsync(entity, "Error", "Error creating certificate: " + e.Message);
                return ResourceControllerResult.RequeueEvent(TimeSpan.FromSeconds(15));
            }
        }

        return null;
    }

    private async Task AddDefaultConfig(SentryDeployment entity)
    {
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

        var cronConfigMap = await _client.Get<V1ConfigMap>("sentry-cron", entity.Namespace());
        if (cronConfigMap == null)
        {
            cronConfigMap = new V1ConfigMap
            {
                Metadata = new V1ObjectMeta
                {
                    Name = "sentry-cron",
                    NamespaceProperty = entity.Namespace()
                },
                Data = new Dictionary<string, string>
                {
                    ["entrypoint.sh"] = """
                                        declare -p | grep -Ev 'BASHOPTS|BASH_VERSINFO|EUID|PPID|SHELLOPTS|UID' >/container.env

                                        { for cron_job in "$@"; do echo -e "SHELL=/bin/bash
                                        BASH_ENV=/container.env
                                        ${cron_job} > /proc/1/fd/1 2>/proc/1/fd/2"; done; } |
                                          sed --regexp-extended 's/\\(.)/\1/g' |
                                          crontab -
                                        crontab -l
                                        exec cron -f -l -L 15
                                        """.Replace("\r\n", "\n"), // Make sure we don't do Windows line endings on Linux!
                }
            };
            await _client.Create(cronConfigMap);
        }
        
        // Check if sentry-config exists; if it doesn't, download config from GitHub and create ConfigMap with config.yml, docker-entrypoint.sh, requirements.txt and sentry.conf.py
        var configMap = await _client.Get<V1Secret>("sentry-config", entity.Namespace());
        if (configMap == null)
        {
            var configUrl = $"https://raw.githubusercontent.com/getsentry/self-hosted/{entity.Spec.GetVersion()}/sentry/config.example.yml";
            var entrypointUrl = $"https://raw.githubusercontent.com/getsentry/self-hosted/{entity.Spec.GetVersion()}/sentry/entrypoint.sh";
            var sentryConfPyUrl = $"https://raw.githubusercontent.com/getsentry/self-hosted/{entity.Spec.GetVersion()}/sentry/sentry.conf.example.py";
            
            var configRaw = await _httpClient.GetStringAsync(configUrl);
            var entrypointRaw = await _httpClient.GetStringAsync(entrypointUrl);
            var sentryConfPyRaw = await _httpClient.GetStringAsync(sentryConfPyUrl);
            
            // Generate a 50 character secret key
            // Allowed characters: "a-z0-9@#%^&*(-_=+)"
            var secretKey = GenerateSecretKey();
            configRaw = configRaw.Replace("!!changeme!!", secretKey);
            
            configMap = new V1Secret
            {
                Metadata = new V1ObjectMeta
                {
                    Name = "sentry-config",
                    NamespaceProperty = entity.Namespace()
                },
                StringData = new Dictionary<string, string>
                {
                    ["config.yml"] = configRaw,
                    ["entrypoint.sh"] = entrypointRaw,
                    ["sentry.conf.py"] = sentryConfPyRaw,
                    ["requirements.txt"] = ""
                }
            };
            
            await _client.Create(configMap);
        }
        
        var snubaEnvConfigMap = await _client.Get<V1ConfigMap>("snuba-env", entity.Namespace());
        if (snubaEnvConfigMap == null)
        {
            snubaEnvConfigMap = new V1ConfigMap
            {
                Metadata = new V1ObjectMeta
                {
                    Name = "snuba-env",
                    NamespaceProperty = entity.Namespace()
                },
                Data = new Dictionary<string, string>
                {
                    ["UWSGI_DIE_ON_TERM"] = "true",
                    ["UWSGI_NEED_APP"] = "true",
                    ["REDIS_PORT"] = "6379",
                    ["CLICKHOUSE_PORT"] = "9000",
                    ["SNUBA_SETTINGS"] = "docker",
                    ["UWSGI_MAX_REQUESTS"] = "10000",
                    ["UWSGI_IGNORE_WRITE_ERRORS"] = "true",
                    ["REDIS_HOST"] = "redis",
                    ["UWSGI_DISABLE_WRITE_EXCEPTION"] = "true",
                    ["CLICKHOUSE_HOST"] = "clickhouse",
                    ["UWSGI_ENABLE_THREADS"] = "true",
                    ["UWSGI_DISABLE_LOGGING"] = "true",
                    ["DEFAULT_BROKERS"] = "kafka-service:9092",
                    ["UWSGI_IGNORE_SIGPIPE"] = "true"
                }
            };
            await _client.Create(snubaEnvConfigMap);
        }
        
        await InitAndGetRelayConfigMap(entity);
    }

    private string GenerateSecretKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(256);
        return Convert.ToBase64String(bytes).Substring(0, 50);
    }

    /// <summary>
    /// Check if relay-conf exists; if it doesn't, download config from GitHub and create config.yml
    /// </summary>
    /// <param name="entity"></param>
    /// <returns></returns>
    private async Task<V1ConfigMap> InitAndGetRelayConfigMap(SentryDeployment entity)
    {
        var relayConfigMap = await _client.Get<V1ConfigMap>("relay-conf", entity.Namespace());
        if (relayConfigMap == null)
        {
            var configUrl = $"https://raw.githubusercontent.com/getsentry/self-hosted/{entity.Spec.GetVersion()}/relay/config.example.yml";

            var configRaw = await _httpClient.GetStringAsync(configUrl);

            relayConfigMap = new V1ConfigMap
            {
                Metadata = new V1ObjectMeta
                {
                    Name = "relay-conf",
                    NamespaceProperty = entity.Namespace()
                },
                Data = new Dictionary<string, string>
                {
                    ["config.yml"] = configRaw
                }
            };

            await _client.Create(relayConfigMap);
        }

        _ = Task.Run(() => WaitForRelayAndGenerateCredentials(entity));
        
        return relayConfigMap;
    }

    private async Task WaitForRelayAndGenerateCredentials(SentryDeployment entity)
    {
        var pods = await _client.List<V1Pod>(entity.Namespace(), labelSelector: $"app.kubernetes.io/managed-by=sentry-operator,app.kubernetes.io/name=relay");
        var pod = pods.First();
        while (pod.Status.Phase != "Running")
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            pods = await _client.List<V1Pod>(entity.Namespace(), labelSelector: $"app.kubernetes.io/managed-by=sentry-operator,app.kubernetes.io/name=relay");
            pod = pods.First();
        }

        await GenerateRelayCredentials(entity);
    }

    public async Task GenerateRelayCredentials(SentryDeployment entity)
    {
        var configMap = await InitAndGetRelayConfigMap(entity);
        
        // Find relay pod by label
        var pods = await _client.List<V1Pod>(entity.Namespace(), labelSelector: $"app.kubernetes.io/managed-by=sentry-operator,app.kubernetes.io/name=relay");
        var pod = pods.First();

        // Timeout after 30 seconds with cancellation token
        var cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
        
        await _client.ApiClient.NamespacedPodExecAsync(
            pod.Name(),
            pod.Namespace(),
            "relay",
            new List<string>
            {
                "relay credentials generate --stdout"
            }, false, async (@in, @out, err) =>
            {
                using var sr = new StreamReader(@out);
                using var srErr = new StreamReader(err);
                var credentials = await sr.ReadToEndAsync(cancellationToken);
                var error = await srErr.ReadToEndAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(error))
                {
                    _logger.LogError("Error generating credentials: {Error}", error);
                    
                    entity = (await _client.Get<SentryDeployment>(entity.Name(), entity.Namespace()))!;
                    entity.Status.Status = "Error";
                    entity.Status.Message = error;
                    await _client.UpdateStatus(entity);
                    
                    return;
                }
                configMap.Data["credentials.json"] = credentials;
                await _client.Update(configMap);
            }, cancellationToken);
    }

    private async Task<bool> InstallKafkaTopics(SentryDeployment entity)
    {
        const string bitnamiCommand = "/opt/bitnami/kafka/bin/kafka-topics.sh --create --bootstrap-server kafka:9092 --topic ";
        const string kafkaTopicsDefault = "ingest-attachments ingest-transactions ingest-events ingest-replay-recordings profiles ingest-occurrences";
        var kafkaTopicsScriptUrl = $"https://raw.githubusercontent.com/getsentry/self-hosted/{entity.Spec.GetVersion()}/install/create-kafka-topics.sh";

        string kafkaTopics;
        try
        {
            var kafkaTopicsScriptRaw = await _httpClient.GetStringAsync(kafkaTopicsScriptUrl);
        
            // Find the line that starts with 'NEEDED_KAFKA_TOPICS=' and extract the topics surrounded by a "
            var topicsRegex = new Regex(@"NEEDED_KAFKA_TOPICS=""(.*)""");
            var topicsMatch = topicsRegex.Match(kafkaTopicsScriptRaw);
            kafkaTopics = topicsMatch.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(kafkaTopics))
            {
                kafkaTopics = kafkaTopicsDefault;
            }
        }
        catch
        {
            kafkaTopics = kafkaTopicsDefault;
        }
        
        var topics = kafkaTopics.Split(" ");
        
        var pods = await _client.List<V1Pod>(labelSelector: "app.kubernetes.io/managed-by=sentry-operator,app.kubernetes.io/name=kafka");
        var image = pods.First().Spec.Containers.First().Image;

        if (image.Contains("bitnami"))
        {
            var cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
            foreach (var topic in topics)
            {
                await _client.ApiClient.NamespacedPodExecAsync(
                    pods.First().Name(),
                    pods.First().Namespace(),
                    pods.First().Spec.Containers.First().Name,
                    new List<string>
                    {
                        bitnamiCommand + topic
                    }, false, async (@in, @out, err) =>
                    {
                        using var sr = new StreamReader(@out);
                        using var srErr = new StreamReader(err);
                        var output = await sr.ReadToEndAsync(cancellationToken);
                        var error = await srErr.ReadToEndAsync(cancellationToken);
                        if (!string.IsNullOrWhiteSpace(error))
                        {
                            _logger.LogError("Error creating Kafka topics: {Error}", error);
                            return;
                        }
                        _logger.LogInformation("Kafka topics created: {Output}", output);
                    }, cancellationToken);
            }
            
            return true;
        }
        else return false;
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

    private async Task<List<IKubernetesObject<V1ObjectMeta>>> FetchAndConvertDockerCompose(SentryDeployment entity)
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
        dockerComposeRaw = (entity.Spec.Config ?? new()).ReplaceVariables(dockerComposeRaw, entity.Spec.Version ?? "nightly");
        return _dockerComposeConverter.Convert(dockerComposeRaw, entity);
    }
}

