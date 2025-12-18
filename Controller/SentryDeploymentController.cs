using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using k8s;
using k8s.Models;
using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Events;
using KubeOps.Abstractions.Finalizer;
using KubeOps.Abstractions.Rbac;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Caching.Distributed;
using SentryOperator.Docker;
using SentryOperator.Entities;
using SentryOperator.Extensions;
using SentryOperator.Finalizer;
using SentryOperator.Services;

namespace SentryOperator.Controller;

[EntityRbac(typeof(SentryDeployment), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Pod), Verbs = RbacVerb.Create | RbacVerb.Delete | RbacVerb.Patch | RbacVerb.Update | RbacVerb.Get | RbacVerb.List)]
[EntityRbac(typeof(V1Deployment), Verbs = RbacVerb.Create | RbacVerb.Delete | RbacVerb.Patch | RbacVerb.Update | RbacVerb.Get | RbacVerb.List)]
[EntityRbac(typeof(V1StatefulSet), Verbs = RbacVerb.Create | RbacVerb.Delete | RbacVerb.Patch | RbacVerb.Update | RbacVerb.Get | RbacVerb.List)]
[EntityRbac(typeof(V1Service), Verbs = RbacVerb.Create | RbacVerb.Delete | RbacVerb.Patch | RbacVerb.Update | RbacVerb.Get | RbacVerb.List)]
[EntityRbac(typeof(V1Secret), Verbs = RbacVerb.Create | RbacVerb.Delete | RbacVerb.Patch | RbacVerb.Update | RbacVerb.Get | RbacVerb.List)]
[EntityRbac(typeof(V1ConfigMap), Verbs = RbacVerb.Create | RbacVerb.Delete | RbacVerb.Patch | RbacVerb.Update | RbacVerb.Get | RbacVerb.List)]
[GenericRbac(Resources = new[] { "certificates" }, Groups = new[] { "cert-manager.io" }, Verbs = RbacVerb.Get | RbacVerb.Delete | RbacVerb.Patch | RbacVerb.Create)]
public class SentryDeploymentController : IEntityController<SentryDeployment>
{
    public const string DockerComposeUrl = "https://raw.githubusercontent.com/getsentry/self-hosted/master/docker-compose.yml";
    private readonly ILogger<SentryDeploymentController> _logger;
    private readonly EntityFinalizerAttacher<SentryDeploymentFinalizer, SentryDeployment> _finalizer;
    private readonly RemoteFileService _remoteFileService;
    private readonly IKubernetesClient _client;
    private readonly EventPublisher _eventPublisher;
    private readonly DockerComposeConverter _dockerComposeConverter;

    public SentryDeploymentController(ILogger<SentryDeploymentController> logger, EntityFinalizerAttacher<SentryDeploymentFinalizer, SentryDeployment> finalizer,
        RemoteFileService remoteFileService, EventPublisher eventPublisher, DockerComposeConverter dockerComposeConverter, IKubernetesClient client)
    {
        _logger = logger;
        _finalizer = finalizer;
        _remoteFileService = remoteFileService;
        _client = client;
        _eventPublisher = eventPublisher;
        _dockerComposeConverter = dockerComposeConverter;
    }

    public async Task ReconcileAsync(SentryDeployment entity, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Entity {Name} called {ReconcileAsyncName}", entity.Name(), nameof(ReconcileAsync));
        await _finalizer(entity, cancellationToken);

        var resources = await FetchAndConvertDockerCompose(entity);

        await AddDefaultConfig(entity);

        // We don't need to query the resources if the status has the same checksum as the generated spec
        var configChecksum = resources.GetChecksum();
        _logger.LogInformation("Entity {Name} expected checksum: {Checksum}, actual checksum: {ActualChecksum}", entity.Name(), configChecksum, entity.Status.LastVersion);
        if (entity.Status.LastVersion == configChecksum && configChecksum != null)
        {
            return;
        }

        var services = resources.OfType<V1Service>().ToList();
        var deployments = resources.OfType<V1Deployment>().ToList();
        var statefulSets = resources.OfType<V1StatefulSet>().ToList();

        var actualServices = await _client.ListAsync<V1Service>(entity.Namespace(), cancellationToken: cancellationToken);
        var actualDeployments = await _client.ListAsync<V1Deployment>(entity.Namespace(), cancellationToken: cancellationToken);
        var actualStatefulSets = await _client.ListAsync<V1StatefulSet>(entity.Namespace(), cancellationToken: cancellationToken);

        if (!CheckIfUpdateIsNeeded(services, actualServices, deployments, actualDeployments, statefulSets, actualStatefulSets, entity))
        {
            if (entity.Status.Status != "Ready" || string.IsNullOrWhiteSpace(entity.Status.LastVersion))
            {
                entity.Status.Status = "Ready";
                entity.Status.Message = "Sentry deployment is ready";
                entity.Status.LastVersion = configChecksum;
                await _client.UpdateStatusAsync(entity, CancellationToken.None);
            }

            return;
        }

        entity.Status.Status = "Updating";
        entity.Status.Message = "Updating Sentry deployment";
        await _client.UpdateStatusAsync(entity, CancellationToken.None);

        foreach (var service in services)
        {
            var checksum = service.GetChecksum();
            service.SetLabel("sentry-operator/checksum", checksum);
            var svc = actualServices.FirstOrDefault(s => s.Name() == service.Name());
            if (svc == null)
            {
                service.AddOwnerReference(entity.MakeOwnerReference());
                await _client.CreateAsync(service, CancellationToken.None);
            }
            else if (svc.GetLabel("app.kubernetes.io/managed-by") == "sentry-operator")
            {
                if (svc.GetLabel("sentry-operator/checksum") != checksum)
                {
                    service.Metadata.ResourceVersion = svc.Metadata.ResourceVersion;
                    service.AddOwnerReference(entity.MakeOwnerReference());
                    await _client.UpdateAsync(service, CancellationToken.None);
                }
            }
        }


        foreach (var deployment in deployments)
        {
            var checksum = deployment.GetChecksum();
            deployment.SetLabel("sentry-operator/checksum", checksum);
            var actualDeployment = actualDeployments.FirstOrDefault(d => d.Name() == deployment.Name());
            if (actualDeployment == null)
            {
                deployment.AddOwnerReference(entity.MakeOwnerReference());
                await _client.CreateAsync(deployment, CancellationToken.None);
            }
            else if (actualDeployment.GetLabel("app.kubernetes.io/managed-by") == "sentry-operator")
            {
                _logger.LogDebug("Checking deployment {DeploymentName} expected checksum: {DeploymentChecksum}, actual checksum: {ActualChecksum}", deployment.Name(), checksum,
                    actualDeployment.GetLabel("sentry-operator/checksum"));
                if (actualDeployment.GetLabel("sentry-operator/checksum") != checksum || entity.Spec.Version == "nightly")
                {
                    _logger.LogInformation("Updating deployment {DeploymentName}", deployment.Name());
                    deployment.Metadata.ResourceVersion = actualDeployment.Metadata.ResourceVersion;
                    deployment.AddOwnerReference(entity.MakeOwnerReference());
                    await _client.UpdateAsync(deployment, CancellationToken.None);

                    // if (deployment.Metadata.Name == "snuba-api")
                    // {
                    //     await InstallKafkaTopics(entity);
                    // }
                }
            }
        }

        foreach (var deployment in actualDeployments)
        {
            if (deployments.All(d => d.Name() != deployment.Name()) && deployment.GetLabel("app.kubernetes.io/managed-by") == "sentry-operator")
            {
                await _client.DeleteAsync(deployment, CancellationToken.None);
            }
        }

        foreach (var statefulSet in statefulSets)
        {
            var checksum = statefulSet.GetChecksum();
            statefulSet.SetLabel("sentry-operator/checksum", checksum);
            var actualStatefulSet = actualStatefulSets.FirstOrDefault(s => s.Name() == statefulSet.Name());
            if (actualStatefulSet == null)
            {
                statefulSet.AddOwnerReference(entity.MakeOwnerReference());
                await _client.CreateAsync(statefulSet, CancellationToken.None);
            }
            else if (actualStatefulSet.GetLabel("app.kubernetes.io/managed-by") == "sentry-operator")
            {
                _logger.LogDebug("Checking statefulset {StatefulSetName} expected checksum: {StatefulSetChecksum}, actual checksum: {ActualChecksum}", statefulSet.Name(), checksum,
                    actualStatefulSet.GetLabel("sentry-operator/checksum"));
                if (actualStatefulSet.GetLabel("sentry-operator/checksum") != checksum)
                {
                    _logger.LogInformation("Updating statefulset {StatefulSetName}", statefulSet.Name());
                    statefulSet.Metadata.ResourceVersion = actualStatefulSet.Metadata.ResourceVersion;
                    statefulSet.AddOwnerReference(entity.MakeOwnerReference());
                    try
                    {
                        await _client.UpdateAsync(statefulSet, CancellationToken.None);
                    }
                    catch (Exception e)
                    {
                        // We are trying to change something that can't be changed on StatefulSet update, so we need to delete and recreate it
                        _logger.LogError(e, "Error updating statefulset {StatefulSetName}, deleting and recreating it", statefulSet.Name());
                        await _client.DeleteAsync(actualStatefulSet, CancellationToken.None);
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                        statefulSet.AddOwnerReference(entity.MakeOwnerReference());
                        await _client.CreateAsync(statefulSet, CancellationToken.None);
                    }
                }
            }
        }

        foreach (var statefulSet in actualStatefulSets)
        {
            if (statefulSets.All(s => s.Name() != statefulSet.Name()) && statefulSet.GetLabel("app.kubernetes.io/managed-by") == "sentry-operator")
            {
                await _client.DeleteAsync(statefulSet, CancellationToken.None);
            }
        }

        if ((entity.Spec.Certificate?.Install ?? true))
        {
            var result = await AddCertManagerConfig(entity);
            if (!result)
            {
                _logger.LogInformation("Requeuing event");

                entity.Status.Status = "Error creating certificate";
                await _client.UpdateStatusAsync(entity, CancellationToken.None);

                return;
            }
        }

        entity = (await _client.GetAsync<SentryDeployment>(entity.Name(), entity.Namespace(), CancellationToken.None))!;
        entity.Status.Status = "Ready";
        entity.Status.Message = "Sentry deployment is ready";
        entity.Status.LastVersion = configChecksum;
        await _client.UpdateStatusAsync(entity, CancellationToken.None);
        //return ResourceControllerResult.RequeueEvent(TimeSpan.FromSeconds(15));
        return;
    }

    private bool CheckIfUpdateIsNeeded(List<V1Service> services, 
        IList<V1Service> actualServices, 
        List<V1Deployment> deployments, 
        IList<V1Deployment> actualDeployments,
        List<V1StatefulSet> statefulSets,
        IList<V1StatefulSet> actualStatefulSets,
        SentryDeployment entity)
    {
        foreach (var service in services)
        {
            service.AddOwnerReference(entity.MakeOwnerReference());

            var matchingService = actualServices.FirstOrDefault(s => s.Name() == service.Name());
            if (matchingService == null)
            {
                return true;
            }

            if (matchingService.GetLabel("app.kubernetes.io/managed-by") != "sentry-operator")
            {
                continue;
            }

            _logger.LogInformation("Service {ServiceName} expected checksum: {ServiceChecksum}, actual checksum: {ActualChecksum}", service.Name(), service.GetChecksum(),
                matchingService.GetLabel("sentry-operator/checksum"));

            if (matchingService.GetLabel("sentry-operator/checksum") != service.GetChecksum())
            {
                return true;
            }
        }

        foreach (var deployment in deployments)
        {
            deployment.AddOwnerReference(entity.MakeOwnerReference());

            var matchingDeployment = actualDeployments.FirstOrDefault(d => d.Name() == deployment.Name());

            if (matchingDeployment == null) return true;

            if (matchingDeployment.GetLabel("app.kubernetes.io/managed-by") != "sentry-operator")
            {
                continue;
            }

            _logger.LogInformation("Deployment {DeploymentName} expected checksum: {DeploymentChecksum}, actual checksum: {ActualChecksum}", deployment.Name(),
                deployment.GetChecksum(),
                matchingDeployment.GetLabel("sentry-operator/checksum"));

            if (matchingDeployment.GetLabel("sentry-operator/checksum") != deployment.GetChecksum())
            {
                return true;
            }
        }

        foreach (var statefulSet in statefulSets)
        {
            statefulSet.AddOwnerReference(entity.MakeOwnerReference());
            
            var matchingStatefulSet = actualStatefulSets.FirstOrDefault(s => s.Name() == statefulSet.Name());
            if (matchingStatefulSet == null) return true;
            
            if (matchingStatefulSet.GetLabel("app.kubernetes.io/managed-by") != "sentry-operator")
            {
                continue;
            }
            
            _logger.LogInformation("StatefulSet {StatefulSetName} expected checksum: {StatefulSetChecksum}, actual checksum: {ActualChecksum}", statefulSet.Name(), statefulSet.GetChecksum(),
                matchingStatefulSet.GetLabel("sentry-operator/checksum"));

            if (matchingStatefulSet.GetLabel("sentry-operator/checksum") != statefulSet.GetChecksum())
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> AddCertManagerConfig(SentryDeployment entity)
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

    private async Task AddDefaultConfig(SentryDeployment entity)
    {
        var secret = await _client.GetAsync<V1Secret>("sentry-env", entity.Namespace());

        var config = entity.Spec.Config ?? new();
        if (secret == null)
        {
            var secretKeyBytes = RandomNumberGenerator.GetBytes(64);
            var secretKey = Convert.ToBase64String(secretKeyBytes);

            await _client.CreateAsync(new V1Secret()
            {
                Metadata = new V1ObjectMeta
                {
                    Name = "sentry-env",
                    NamespaceProperty = entity.Namespace()
                },
                StringData = new Dictionary<string, string>
                {
                    ["CLICKHOUSE_PORT"] = "9000",
                    ["OPENAI_API_KEY"] = "",
                    ["REDIS_PORT"] = "6379",
                    ["SENTRY_EVENT_RETENTION_DAYS"] = config.EventRetentionDays.ToString(),
                    ["SENTRY_SECRET_KEY"] = secretKey,
                    ["SENTRY_VSTS_CLIENT_ID"] = "",
                    ["SENTRY_VSTS_CLIENT_SECRET"] = "",
                    ["SNUBA"] = "http://snuba-api:1218",
                    ["SENTRY_MAIL_HOST"] = config?.Mail?.From ?? "example.com"
                }
            });
        }

        var cronConfigMap = await _client.GetAsync<V1ConfigMap>("sentry-cron", entity.Namespace());
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
            await _client.CreateAsync(cronConfigMap);
        }

        var configMap = await _client.GetAsync<V1Secret>("sentry-config", entity.Namespace());
        if (configMap == null)
        {
            var (cachedConfigTemplate, secretKey) = await GenerateSentryConfig(entity);

            configMap = new V1Secret
            {
                Metadata = new V1ObjectMeta
                {
                    Name = "sentry-config",
                    NamespaceProperty = entity.Namespace()
                },
                StringData = new Dictionary<string, string>
                {
                    ["secretkey"] = secretKey,
                    ["config.yml"] = cachedConfigTemplate.Config,
                    ["entrypoint.sh"] = cachedConfigTemplate.Entrypoint,
                    ["sentry.conf.py"] = cachedConfigTemplate.SentryConfPy,
                    ["requirements.txt"] = string.Join("\n", entity.Spec.Config?.AdditionalPythonPackages ?? [])
                }
            };

            configMap.AddOwnerReference(entity.MakeOwnerReference());

            await _client.CreateAsync(configMap);
        }
        else if (configMap.IsOwnedBy(entity))
        {
            var (cachedConfigTemplate, secretKey) = await GenerateSentryConfig(entity, configMap.StringData["secretkey"]);

            configMap.StringData["secretkey"] = secretKey;
            configMap.StringData["config.yml"] = cachedConfigTemplate.Config;
            configMap.StringData["entrypoint.sh"] = cachedConfigTemplate.Entrypoint;
            configMap.StringData["sentry.conf.py"] = cachedConfigTemplate.SentryConfPy;
            configMap.StringData["requirements.txt"] = string.Join("\n", entity.Spec.Config?.AdditionalPythonPackages ?? []);

            var hash = configMap.GetChecksum();
            if (hash != configMap.GetLabel("sentry-operator/checksum"))
            {
                configMap.SetLabel("sentry-operator/checksum", hash);
                await _client.UpdateAsync(configMap);
            }
        }

        var snubaEnvConfigMap = await _client.GetAsync<V1ConfigMap>("snuba-env", entity.Namespace());
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
                    ["CLICKHOUSE_HOST"] = "clickhouse",
                    ["CLICKHOUSE_PORT"] = "9000",
                    ["SNUBA_SETTINGS"] = "docker",
                    ["UWSGI_MAX_REQUESTS"] = "10000",
                    ["UWSGI_IGNORE_WRITE_ERRORS"] = "true",
                    ["REDIS_HOST"] = "redis",
                    ["UWSGI_DISABLE_WRITE_EXCEPTION"] = "true",
                    ["UWSGI_ENABLE_THREADS"] = "true",
                    ["UWSGI_DISABLE_LOGGING"] = "true",
                    ["DEFAULT_BROKERS"] = "kafka-service:9092",
                    ["UWSGI_IGNORE_SIGPIPE"] = "true"
                }
            };
            await _client.CreateAsync(snubaEnvConfigMap);
        }

        await InitAndGetRelayConfigMap(entity);
    }

    private async Task<(ConfigTemplate cachedConfigTemplate, string secretKey)> GenerateSentryConfig(SentryDeployment entity, string? secretKey = null)
    {
        // Check if sentry-config exists; if it doesn't, download config from GitHub and create ConfigMap with config.yml, docker-entrypoint.sh, requirements.txt and sentry.conf.py
        var configUrl = $"https://raw.githubusercontent.com/getsentry/self-hosted/{entity.Spec.GetVersion()}/sentry/config.example.yml";
        var entrypointUrl = $"https://raw.githubusercontent.com/getsentry/self-hosted/{entity.Spec.GetVersion()}/sentry/entrypoint.sh";
        var sentryConfPyUrl = $"https://raw.githubusercontent.com/getsentry/self-hosted/{entity.Spec.GetVersion()}/sentry/sentry.conf.example.py";

        var configRaw = await _remoteFileService.GetAsync(configUrl);
        var entrypointRaw = await _remoteFileService.GetAsync(entrypointUrl);
        var sentryConfPyRaw = await _remoteFileService.GetAsync(sentryConfPyUrl);

        var cachedConfigTemplate = new ConfigTemplate
        {
            Config = configRaw,
            Entrypoint = entrypointRaw,
            SentryConfPy = sentryConfPyRaw
        };


        // Generate a 50 character secret key
        // Allowed characters: "a-z0-9@#%^&*(-_=+)"
        secretKey ??= GenerateSecretKey();
        cachedConfigTemplate.Config = cachedConfigTemplate.Config.Replace("!!changeme!!", secretKey);

        // Add mail settings
        var mailConfig = entity.Spec.Config?.Mail;
        if (mailConfig != null)
        {
            var mailConfigRegex = new Regex(@"mail.host: 'smtp'", RegexOptions.Singleline);
            var mailSettings = new StringBuilder();
            mailSettings.AppendLine($"mail.host: '{mailConfig.Host ?? "smtp"}'");
            mailSettings.AppendLine($"mail.port: {mailConfig.Port}");
            mailSettings.AppendLine($"mail.username: '{mailConfig.Username}'");
            mailSettings.AppendLine($"mail.password: '{mailConfig.Password}'");
            if (mailConfig.UseTLS)
            {
                mailSettings.AppendLine("mail.use-tls: true");
            }

            if (mailConfig.UseSSL)
            {
                mailSettings.AppendLine("mail.use-ssl: true");
            }

            if (mailConfig.EnableReplies)
            {
                mailSettings.AppendLine("mail.enable-replies: true");
            }

            if (!string.IsNullOrWhiteSpace(mailConfig.From))
            {
                mailSettings.AppendLine($"mail.from: '{mailConfig.From}'");
            }

            if (!string.IsNullOrWhiteSpace(mailConfig.MailgunApiKey))
            {
                mailSettings.AppendLine($"mail.mailgun-api-key: '{mailConfig.MailgunApiKey}'");
            }

            mailConfigRegex.Replace(cachedConfigTemplate.Config, mailSettings.ToString());
        }

        // Replace Postgres config
        var postgresConfig = entity.Spec.Config?.Postgres ?? new();
        var postgresConfigRegex = new Regex(@"DATABASES = \{.+?\}\s*\}", RegexOptions.Singleline);

        cachedConfigTemplate.SentryConfPy = postgresConfigRegex
            .Replace(cachedConfigTemplate.SentryConfPy, $$"""
                                                          DATABASES = {
                                                              "default": {
                                                                  "ENGINE": "{{postgresConfig.Engine ?? "sentry.db.postgres"}}",
                                                                  "NAME": "{{postgresConfig.Name ?? "postgres"}}",
                                                                  "USER": "{{postgresConfig.User ?? "postgres"}}",
                                                                  "PASSWORD": "{{postgresConfig.Password ?? ""}}",
                                                                  "HOST": "{{postgresConfig.Host ?? "postgres"}}",
                                                                  "PORT": "{{postgresConfig.Port ?? "5432"}}"
                                                              }
                                                          }
                                                          """);

        // Replace Redis config
        var redisConfig = entity.Spec.Config?.Redis ?? new[] { new RedisConfig() };
        var redisConfigRegex = new Regex(@"SENTRY_OPTIONS\[""redis.clusters""\] = \{.+?\}\s+^\}", RegexOptions.Singleline | RegexOptions.Multiline);

        cachedConfigTemplate.SentryConfPy = redisConfigRegex
            .Replace(cachedConfigTemplate.SentryConfPy, GenerateRedisConfig(redisConfig));

        var additionalFlags = entity.Spec.Config?.AdditionalFeatureFlags ?? Array.Empty<string>();

        if (additionalFlags.Any())
        {
            const string featuresStart = "for feature in (";

            // Prepend the feature flags to the sentry.conf.py file after the start string
            var featuresIndex = cachedConfigTemplate.SentryConfPy.IndexOf(featuresStart, StringComparison.Ordinal);
            if (featuresIndex != -1)
            {
                var featuresEnd = cachedConfigTemplate.SentryConfPy.IndexOf(")", featuresIndex, StringComparison.Ordinal);
                var features = string.Join(", ", additionalFlags.Select(f => $"'{f}'"));
                cachedConfigTemplate.SentryConfPy = cachedConfigTemplate.SentryConfPy.Insert(featuresEnd, $"{features}");
            }
        }

        const string internalIPsDefinition = "INTERNAL_SYSTEM_IPS = (get_internal_network(),)";
        const string extendedDefinition = "INTERNAL_SYSTEM_IPS = (get_internal_network(),'172.30.0.0/16','10.0.0.0/8', '192.168.0.0/16')";

        cachedConfigTemplate.SentryConfPy = cachedConfigTemplate.SentryConfPy.Replace(internalIPsDefinition, extendedDefinition);

        var SSLTLSConfig = """
                           # SECURE_PROXY_SSL_HEADER = ('HTTP_X_FORWARDED_PROTO', 'https')
                           # USE_X_FORWARDED_HOST = True
                           # SESSION_COOKIE_SECURE = True
                           # CSRF_COOKIE_SECURE = True
                           # SOCIAL_AUTH_REDIRECT_IS_HTTPS = True
                           """.Split("\n").Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s));

        // Replace the above lines without the comments
        foreach (var line in SSLTLSConfig)
        {
            cachedConfigTemplate.SentryConfPy = cachedConfigTemplate.SentryConfPy.Replace(line, line[2..]);
        }

        return (cachedConfigTemplate, secretKey);
    }

    private string GenerateRedisConfig(RedisConfig[] redisConfig)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SENTRY_OPTIONS[\"redis.clusters\"] = {");
        sb.AppendLine("    \"default\": {");
        sb.AppendLine("        \"hosts\": {");
        for (var i = 0; i < redisConfig.Length; i++)
        {
            sb.Append($$"""
                        {{i}}: {
                            "host": "{{redisConfig[i].Host ?? "redis"}}",
                            "port": "{{redisConfig[i].Port ?? "6379"}}",
                            "password": "{{redisConfig[i].Password ?? ""}}",
                            "db": "{{redisConfig[i].Database ?? "0"}}"
                        }
                        """);
            if (i < redisConfig.Length - 1)
            {
                sb.AppendLine(",");
            }
        }

        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
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
        var relayConfigMap = await _client.GetAsync<V1ConfigMap>("relay-conf", entity.Namespace());
        if (relayConfigMap == null)
        {
            var configUrl = $"https://raw.githubusercontent.com/getsentry/self-hosted/{entity.Spec.GetVersion()}/relay/config.example.yml";

            var configRaw = await _remoteFileService.GetAsync(configUrl);

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

            await _client.CreateAsync(relayConfigMap);
        }

        _ = Task.Run(() => WaitForRelayAndGenerateCredentials(entity));

        return relayConfigMap;
    }

    private async Task WaitForRelayAndGenerateCredentials(SentryDeployment entity)
    {
        var pods = await _client.ListAsync<V1Pod>(entity.Namespace(), labelSelector: $"app.kubernetes.io/managed-by=sentry-operator,app.kubernetes.io/name=relay");
        var pod = pods.First();
        while (pod.Status.Phase != "Running")
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            pods = await _client.ListAsync<V1Pod>(entity.Namespace(), labelSelector: $"app.kubernetes.io/managed-by=sentry-operator,app.kubernetes.io/name=relay");
            pod = pods.First();
        }

        await GenerateRelayCredentials(entity);
    }

    public async Task GenerateRelayCredentials(SentryDeployment entity)
    {
        var configMap = await InitAndGetRelayConfigMap(entity);

        // Find relay pod by label
        var pods = await _client.ListAsync<V1Pod>(entity.Namespace(), labelSelector: $"app.kubernetes.io/managed-by=sentry-operator,app.kubernetes.io/name=relay");
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

                    entity = (await _client.GetAsync<SentryDeployment>(entity.Name(), entity.Namespace(), CancellationToken.None))!;
                    entity.Status.Status = "Error";
                    entity.Status.Message = error;
                    await _client.UpdateStatusAsync(entity, CancellationToken.None);

                    return;
                }

                configMap.Data["credentials.json"] = credentials;
                await _client.UpdateAsync(configMap, CancellationToken.None);
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
            var kafkaTopicsScriptRaw = await _remoteFileService.GetAsync(kafkaTopicsScriptUrl);

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

        var pods = await _client.ListAsync<V1Pod>(labelSelector: "app.kubernetes.io/name=kafka");
        var image = pods.FirstOrDefault()?.Spec.Containers.First().Image;

        if (image?.Contains("bitnami") ?? false)
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

    public async Task DeletedAsync(SentryDeployment entity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation("Entity {Name} called {DeletedAsyncName}", entity.Name(), nameof(DeletedAsync));

        var deployments = await _client.ListAsync<V1Deployment>(entity.Namespace(), labelSelector: $"app.kubernetes.io/managed-by=sentry-operator",
            cancellationToken: cancellationToken);
        foreach (var deployment in deployments)
        {
            await _client.DeleteAsync(deployment, CancellationToken.None);
        }

        var services = await _client.ListAsync<V1Service>(entity.Namespace(), labelSelector: $"app.kubernetes.io/managed-by=sentry-operator", CancellationToken.None);
        foreach (var service in services)
        {
            await _client.DeleteAsync(service, CancellationToken.None);
        }

        var configMaps = await _client.ListAsync<V1ConfigMap>(entity.Namespace(), labelSelector: $"app.kubernetes.io/managed-by=sentry-operator", CancellationToken.None);
        foreach (var configMap in configMaps)
        {
            await _client.DeleteAsync(configMap, CancellationToken.None);
        }

        var secrets = await _client.ListAsync<V1Secret>(entity.Namespace(), labelSelector: $"app.kubernetes.io/managed-by=sentry-operator", CancellationToken.None);
        foreach (var secret in secrets)
        {
            await _client.DeleteAsync(secret, CancellationToken.None);
        }

        var certName = entity.Name() + "-certificate";
        var certificate = await _client.GetAsync<Certificate>(certName, entity.Namespace(), CancellationToken.None);
        if (certificate != null)
        {
            await _client.DeleteAsync(certificate, CancellationToken.None);
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

        var dockerComposeRaw = await _remoteFileService.GetAsync(dockerComposeUrl);

        dockerComposeRaw = (entity.Spec.Config ?? new()).ReplaceVariables(dockerComposeRaw, entity.Spec.Version ?? "nightly");
        return _dockerComposeConverter.Convert(dockerComposeRaw, entity);
    }
}

public class ConfigTemplate
{
    public required string Config { get; set; }
    public required string Entrypoint { get; set; }
    public required string SentryConfPy { get; set; }
}