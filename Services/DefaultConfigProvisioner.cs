using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using k8s;
using k8s.Models;
using KubeOps.Abstractions.Entities;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using SentryOperator.Entities;
using SentryOperator.Extensions;
using SentryOperator.Controller;

namespace SentryOperator.Services;

public class DefaultConfigProvisioner : IDefaultConfigProvisioner
{
    private const string ChecksumLabel = "sentry-operator/checksum";
    private readonly IKubernetesClient _client;
    private readonly ILogger<DefaultConfigProvisioner> _logger;
    private readonly RemoteFileService _remoteFileService;

    public DefaultConfigProvisioner(IKubernetesClient client, ILogger<DefaultConfigProvisioner> logger, RemoteFileService remoteFileService)
    {
        _client = client;
        _logger = logger;
        _remoteFileService = remoteFileService;
    }

public async Task EnsureAsync(SentryDeployment entity)
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
        configMap = CreateManagedSentryConfigSecret(entity, cachedConfigTemplate, secretKey);
        await _client.CreateAsync(configMap);
    }
    else if (configMap.IsOwnedBy(entity))
    {
        var (cachedConfigTemplate, secretKey) = await GenerateSentryConfig(entity, configMap.StringData["secretkey"]);
        var desiredConfigMap = CreateManagedSentryConfigSecret(entity, cachedConfigTemplate, secretKey);
        if (desiredConfigMap.GetLabel(ChecksumLabel) != configMap.GetLabel(ChecksumLabel))
        {
            desiredConfigMap.Metadata.ResourceVersion = configMap.Metadata.ResourceVersion;
            await _client.UpdateAsync(desiredConfigMap);
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
    
    var snubaHealthCheckConfigMap = await _client.GetAsync<V1ConfigMap>("snuba-healthcheck", entity.Namespace());
    if (snubaHealthCheckConfigMap == null)
    {
        var snubaApiHealthCheckUrl = $"https://raw.githubusercontent.com/getsentry/self-hosted/{entity.Spec.GetVersion()}/snuba/api_healthcheck.py";
        var snubaApiHealthCheckRaw = await _remoteFileService.GetAsync(snubaApiHealthCheckUrl);
        snubaHealthCheckConfigMap = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta
            {
                Name = "snuba-healthcheck",
                NamespaceProperty = entity.Namespace()
            },
            Data = new Dictionary<string, string>
            {
                ["api_healthcheck.py"] = snubaApiHealthCheckRaw
            }
        };
        await _client.CreateAsync(snubaHealthCheckConfigMap);
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

private static V1Secret CreateManagedSentryConfigSecret(
    SentryDeployment entity,
    ConfigTemplate cachedConfigTemplate,
    string secretKey)
{
    var secret = new V1Secret
    {
        Metadata = new V1ObjectMeta
        {
            Name = "sentry-config",
            NamespaceProperty = entity.Namespace(),
        },
        StringData = new Dictionary<string, string>
        {
            ["secretkey"] = secretKey,
            ["config.yml"] = cachedConfigTemplate.Config,
            ["entrypoint.sh"] = cachedConfigTemplate.Entrypoint,
            ["sentry.conf.py"] = cachedConfigTemplate.SentryConfPy,
            ["requirements.txt"] = string.Join("\n", entity.Spec.Config?.AdditionalPythonPackages ?? []),
        },
    };

    secret.AddOwnerReference(entity.MakeOwnerReference());
    secret.SetLabel(ChecksumLabel, secret.GetChecksum());
    return secret;
}

public static string GenerateRedisConfig(RedisConfig[] redisConfig)
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

}