using System.Net;
using System.Text;
using k8s.Models;
using KubeOps.Abstractions.Events;
using KubeOps.Abstractions.Finalizer;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using SentryOperator.Controller;
using SentryOperator.Docker;
using SentryOperator.Docker.Compose;
using SentryOperator.Docker.Converters;
using SentryOperator.Entities;
using SentryOperator.Finalizer;
using SentryOperator.Services;

namespace SentryOperator.Tests;

internal static class TestFixtureData
{
    private static readonly string ComposeFixturePath = Path.Combine(AppContext.BaseDirectory, "TestData", "docker-compose.yml");

    public static string GetComposeYaml() => File.ReadAllText(ComposeFixturePath);

    public static string GetResolvedComposeYaml(SentryDeployment deployment) =>
        (deployment.Spec.Config ?? new SentryDeploymentConfig())
        .ReplaceVariables(GetComposeYaml(), deployment.Spec.GetVersion());

    public static DockerCompose ParseCompose(SentryDeployment? deployment = null)
    {
        deployment ??= CreateDeployment();
        return new YamlDockerComposeParser()
            .Parse(GetResolvedComposeYaml(deployment), deployment.Spec.DockerComposeOverrides);
    }

    public static DockerService GetFixtureService(
        string serviceName,
        SentryDeployment? deployment = null,
        Action<DockerService>? mutate = null)
    {
        var service = ParseCompose(deployment).Services![serviceName];
        mutate?.Invoke(service);
        return service;
    }

    public static IEnumerable<IDockerContainerConverter> CreateConverters()
    {
        return typeof(IDockerContainerConverter).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract &&
                           typeof(IDockerContainerConverter).IsAssignableFrom(type))
            .Select(type => (IDockerContainerConverter)Activator.CreateInstance(type)!);
    }

    public static DockerComposeConverter CreateDockerComposeConverter() =>
        new(NullLogger<DockerComposeConverter>.Instance, CreateConverters());

    public static SentryDeployment CreateDeployment(Action<SentryDeployment>? configure = null)
    {
        var deployment = new SentryDeployment
        {
            ApiVersion = "sentry.io/v1",
            Kind = "SentryDeployment",
            Metadata = new V1ObjectMeta
            {
                Name = "sentry",
                NamespaceProperty = "default",
                Uid = "00000000-0000-0000-0000-000000000001",
            },
            Spec = new SentryDeployment.SentryDeploymentSpec
            {
                Version = "23.6.1",
                Config = new SentryDeploymentConfig(),
                Environment = new Dictionary<string, string>
                {
                    ["GEOIPUPDATE_LICENSE_KEY"] = "test-license",
                    ["GEOIPUPDATE_ACCOUNT_ID"] = "test-account",
                    ["GEOIPUPDATE_EDITION_IDS"] = "GeoLite2-City",
                },
            },
            Status = new SentryDeployment.SentryDeploymentStatus(),
        };

        configure?.Invoke(deployment);
        return deployment;
    }

    public static RemoteFileService CreateRemoteFileService(SentryDeployment deployment)
    {
        var version = deployment.Spec.GetVersion();
        var responses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ComposeSourceResolver.DefaultDockerComposeUrl] = GetComposeYaml(),
            [$"https://raw.githubusercontent.com/getsentry/self-hosted/{version}/docker-compose.yml"] = GetComposeYaml(),
            ["https://raw.githubusercontent.com/getsentry/self-hosted/master/docker-compose.yml"] = GetComposeYaml(),
            [$"https://raw.githubusercontent.com/getsentry/self-hosted/{version}/snuba/api_healthcheck.py"] = """
                print("ok")
                """,
            [$"https://raw.githubusercontent.com/getsentry/self-hosted/{version}/sentry/config.example.yml"] = """
                system.secret-key: '!!changeme!!'
                mail.host: 'smtp'
                """,
            [$"https://raw.githubusercontent.com/getsentry/self-hosted/{version}/sentry/entrypoint.sh"] = """
                #!/bin/bash
                exec "$@"
                """,
            [$"https://raw.githubusercontent.com/getsentry/self-hosted/{version}/sentry/sentry.conf.example.py"] = """
                DATABASES = {
                    "default": {
                        "ENGINE": "sentry.db.postgres",
                        "NAME": "postgres",
                        "USER": "postgres",
                        "PASSWORD": "",
                        "HOST": "postgres",
                        "PORT": "5432"
                    }
                }

                SENTRY_OPTIONS["redis.clusters"] = {
                    "default": {
                        "hosts": {
                            0: {
                                "host": "redis",
                                "port": "6379",
                                "password": "",
                                "db": "0"
                            }
                        }
                    }
                }

                INTERNAL_SYSTEM_IPS = (get_internal_network(),)

                # SECURE_PROXY_SSL_HEADER = ('HTTP_X_FORWARDED_PROTO', 'https')
                # USE_X_FORWARDED_HOST = True
                # SESSION_COOKIE_SECURE = True
                # CSRF_COOKIE_SECURE = True
                # SOCIAL_AUTH_REDIRECT_IS_HTTPS = True

                for feature in ():
                    pass
                """,
            [$"https://raw.githubusercontent.com/getsentry/self-hosted/{version}/relay/config.example.yml"] = """
                relay:
                  upstream: sentry
                """,
        };

        var client = new HttpClient(new FixtureHttpMessageHandler(responses));
        return new RemoteFileService(client, new InMemoryDistributedCache());
    }

    public static SentryDeploymentController CreateController(
        InMemoryKubernetesClient client,
        SentryDeployment deployment)
    {
        var remoteFileService = CreateRemoteFileService(deployment);
        var composeSourceResolver = new ComposeSourceResolver(remoteFileService);
        var dockerComposeConverter = CreateDockerComposeConverter();
        var certificateProvisioner = new CertificateProvisioner(
            client,
            CreateNoopEventPublisher(),
            NullLogger<CertificateProvisioner>.Instance);
        var defaultConfigProvisioner = new DefaultConfigProvisioner(
            client,
            NullLogger<DefaultConfigProvisioner>.Instance,
            remoteFileService);

        return new SentryDeploymentController(
            NullLogger<SentryDeploymentController>.Instance,
            CreateNoopFinalizer(),
            remoteFileService,
            certificateProvisioner,
            defaultConfigProvisioner,
            new ManagedResourceCleanup(client),
            composeSourceResolver,
            dockerComposeConverter,
            client,
            new HorizontalPodAutoscalerReconciler(client));
    }

    public static EventPublisher CreateNoopEventPublisher() =>
        (_, _, _, _, _) => Task.CompletedTask;

    public static EntityFinalizerAttacher<SentryDeploymentFinalizer, SentryDeployment> CreateNoopFinalizer() =>
        (entity, _) => Task.FromResult(entity);
}

internal sealed class FixtureHttpMessageHandler : HttpMessageHandler
{
    private readonly IReadOnlyDictionary<string, string> _responses;

    public FixtureHttpMessageHandler(IReadOnlyDictionary<string, string> responses)
    {
        _responses = responses;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString() ?? string.Empty;
        if (_responses.TryGetValue(url, out var content))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "text/plain"),
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            RequestMessage = request,
            Content = new StringContent($"No local fixture response registered for '{url}'.", Encoding.UTF8),
        });
    }
}

internal sealed class InMemoryDistributedCache : IDistributedCache
{
    private readonly Dictionary<string, byte[]> _values = [];
    private readonly object _gate = new();

    public byte[]? Get(string key)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var value) ? value.ToArray() : null;
        }
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
        Task.FromResult(Get(key));

    public void Refresh(string key)
    {
    }

    public Task RefreshAsync(string key, CancellationToken token = default) =>
        Task.CompletedTask;

    public void Remove(string key)
    {
        lock (_gate)
        {
            _values.Remove(key);
        }
    }

    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        Remove(key);
        return Task.CompletedTask;
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        lock (_gate)
        {
            _values[key] = value.ToArray();
        }
    }

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        Set(key, value, options);
        return Task.CompletedTask;
    }
}
