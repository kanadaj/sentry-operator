using k8s;
using k8s.Models;
using SentryOperator.Docker;
using SentryOperator.Docker.Compose;
using SentryOperator.Docker.Converters;
using SentryOperator.Entities;

namespace SentryOperator.Tests;

public class DockerContainerConverterTests
{
    public static TheoryData<ConverterScenario> ConverterScenarios =>
    [
        new(
            "Default converter",
            typeof(DefaultConverter),
            "smtp",
            deployment => TestFixtureData.GetFixtureService("smtp", deployment, service => service.Ports = ["25:25"]),
            null,
            resources =>
            {
                AssertResourceTypes(resources, typeof(V1Deployment), typeof(V1Service));
                Assert.Equal(["smtp", "smtp"], resources.Select(resource => resource.Metadata.Name).ToArray());

                var deployment = Assert.Single(resources.OfType<V1Deployment>());
                var container = Assert.Single(deployment.Spec.Template.Spec.Containers);
                Assert.Contains(container.EnvFrom!, source => source.SecretRef?.Name == "sentry-env");

                var service = Assert.Single(resources.OfType<V1Service>());
                Assert.Equal(25, Assert.Single(service.Spec.Ports).Port);
            },
            false),
        new(
            "Vroom converter",
            typeof(VroomConverter),
            "vroom",
            deployment => TestFixtureData.GetFixtureService("vroom", deployment),
            null,
            resources =>
            {
                var deployment = AssertSingleDeployment(resources, "vroom");
                var container = Assert.Single(deployment.Spec.Template.Spec.Containers);
                Assert.Contains(container.EnvFrom!, source => source.SecretRef?.Name == "sentry-env");
                Assert.Contains(container.Ports!, port => port.ContainerPort == 8085);
            },
            false),
        new(
            "Sentry container converter",
            typeof(SentryContainerConverter),
            "taskscheduler",
            deployment => TestFixtureData.GetFixtureService("taskscheduler", deployment),
            null,
            resources =>
            {
                var deployment = AssertSingleDeployment(resources, "taskscheduler");
                var container = Assert.Single(deployment.Spec.Template.Spec.Containers);
                Assert.Equal("getsentry/sentry:23.6.1", container.Image);
                Assert.Contains("pip install -r /etc/sentry/requirements.txt && exec /docker-entrypoint.sh run taskworker-scheduler", container.Args!);
                Assert.Contains(container.EnvFrom!, source => source.SecretRef?.Name == "sentry-env");
                Assert.Contains(container.VolumeMounts!, mount => mount.Name == "sentry-config" && mount.MountPath == "/etc/sentry/config.yml");
                Assert.Contains(deployment.Spec.Template.Spec.Volumes!, volume => volume.Name == "sentry-config" && volume.Secret?.SecretName == "sentry-config");
                Assert.Contains(deployment.Spec.Template.Spec.Volumes!, volume => volume.Name == "sentry-data");
            }),
        new(
            "Sentry web converter",
            typeof(SentryWebConverter),
            "web",
            deployment => TestFixtureData.GetFixtureService("web", deployment),
            deployment =>
            {
                deployment.Spec.Config!.HealthCheckInterval = "15s";
                deployment.Spec.Config.HealthCheckTimeout = "45s";
                deployment.Spec.Config.HealthCheckRetries = "7";
                deployment.Spec.Config.HealthCheckStartPeriod = "12s";
            },
            resources =>
            {
                var deployment = AssertSingleDeployment(resources, "web");
                var container = Assert.Single(deployment.Spec.Template.Spec.Containers);
                Assert.Contains("pip install -r /etc/sentry/requirements.txt && exec /docker-entrypoint.sh run web", container.Args!);
                Assert.Contains(container.Ports!, port => port.ContainerPort == 9000);
                Assert.Equal("/_health/", container.ReadinessProbe!.HttpGet.Path);
                Assert.Equal(9000, container.ReadinessProbe.HttpGet.Port);
                Assert.Equal(12, container.ReadinessProbe.InitialDelaySeconds);
                Assert.Equal(15, container.ReadinessProbe.PeriodSeconds);
                Assert.Equal(45, container.ReadinessProbe.TimeoutSeconds);
                Assert.Equal(7, container.ReadinessProbe.FailureThreshold);
                Assert.Equal("/_health/", container.LivenessProbe!.HttpGet.Path);

                var initContainer = Assert.Single(deployment.Spec.Template.Spec.InitContainers!);
                Assert.Equal("init-db", initContainer.Name);
                Assert.Contains("upgrade --noinput --create-kafka-topics", Assert.Single(initContainer.Args!));
            }),
        new(
            "Taskworker converter",
            typeof(TaskWorkerConverter),
            "taskworker",
            deployment => TestFixtureData.GetFixtureService("taskworker", deployment),
            deployment =>
            {
                deployment.Spec.Config!.TaskWorkerConcurrency = 8;
                deployment.Spec.Config.TaskWorkerAutoscaling = new TaskWorkerAutoscaling
                {
                    MinReplicas = 2,
                    MaxReplicas = 8,
                    CpuTargetUtilization = 65,
                    ConsumerLagTarget = 250,
                    ExternalMetricName = "kafka_taskworker_lag",
                };
                deployment.Spec.Replicas = new Dictionary<string, int>
                {
                    ["taskbroker"] = 3,
                    ["taskworker"] = 7,
                };
            },
            resources =>
            {
                AssertResourceTypes(resources, typeof(V1Deployment), typeof(V2HorizontalPodAutoscaler));
                Assert.Equal(["taskworker", "taskworker-autoscaler"], resources.Select(resource => resource.Metadata.Name).ToArray());

                var deployment = AssertSingleDeployment(resources, "taskworker");
                Assert.Equal(2, deployment.Spec.Replicas);
                var container = Assert.Single(deployment.Spec.Template.Spec.Containers);
                var args = Assert.Single(container.Args!);
                Assert.Contains("--concurrency=8", args);
                Assert.DoesNotContain("--rpc-host=taskbroker:50051", args);
                Assert.Contains("--rpc-host-list=taskbroker-0.taskbroker:50051,taskbroker-1.taskbroker:50051,taskbroker-2.taskbroker:50051", args);

                var hpa = Assert.Single(resources.OfType<V2HorizontalPodAutoscaler>());
                Assert.Equal("taskworker", hpa.Spec.ScaleTargetRef.Name);
                Assert.Equal(2, hpa.Spec.MinReplicas);
                Assert.Equal(8, hpa.Spec.MaxReplicas);
            }),
        new(
            "Snuba converter",
            typeof(SnubaConverter),
            "snuba-replacer",
            deployment => TestFixtureData.GetFixtureService("snuba-replacer", deployment),
            null,
            resources =>
            {
                var deployment = AssertSingleDeployment(resources, "snuba-replacer");
                var container = Assert.Single(deployment.Spec.Template.Spec.Containers);
                Assert.Contains(container.EnvFrom!, source => source.ConfigMapRef?.Name == "snuba-env");
            }),
        new(
            "Snuba API converter",
            typeof(SnubaApiConverter),
            "snuba-api",
            deployment => TestFixtureData.GetFixtureService("snuba-api", deployment),
            null,
            resources =>
            {
                var deployment = AssertSingleDeployment(resources, "snuba-api");
                Assert.Equal(["snuba-bootstrap", "snuba-migration"], deployment.Spec.Template.Spec.InitContainers!.Select(container => container.Name).ToArray());
                var container = Assert.Single(deployment.Spec.Template.Spec.Containers);
                Assert.Contains(container.EnvFrom!, source => source.SecretRef?.Name == "sentry-env");
                Assert.Contains(container.EnvFrom!, source => source.ConfigMapRef?.Name == "snuba-env");
                Assert.Contains(deployment.Spec.Template.Spec.Volumes!, volume => volume.Name == "snuba-healthcheck" && volume.ConfigMap?.Name == "snuba-healthcheck");
            }),
        new(
            "Snuba consumer converter",
            typeof(SnubaConsumerConverter),
            "snuba-errors-consumer",
            deployment => TestFixtureData.GetFixtureService("snuba-errors-consumer", deployment),
            null,
            resources =>
            {
                var deployment = AssertSingleDeployment(resources, "snuba-errors-consumer");
                var container = Assert.Single(deployment.Spec.Template.Spec.Containers);
                Assert.Contains(container.EnvFrom!, source => source.ConfigMapRef?.Name == "snuba-env");
                Assert.Equal(["--concurrency", "4"], container.Args!.TakeLast(2).ToArray());
            }),
        new(
            "Taskbroker converter",
            typeof(TaskBrokerConverter),
            "taskbroker",
            deployment => TestFixtureData.GetFixtureService("taskbroker", deployment),
            deployment =>
            {
                deployment.Spec.Replicas = new Dictionary<string, int>
                {
                    ["taskbroker"] = 2,
                };
            },
            resources =>
            {
                AssertResourceTypes(resources, typeof(V1StatefulSet));

                var statefulSet = Assert.Single(resources.OfType<V1StatefulSet>());
                Assert.Equal("taskbroker", statefulSet.Metadata.Name);
                Assert.Equal(2, statefulSet.Spec.Replicas);
                Assert.Equal("taskbroker", statefulSet.Spec.ServiceName);
                Assert.Equal("5Gi", statefulSet.Spec.VolumeClaimTemplates.Single().Spec.Resources.Requests["storage"].ToString());
                Assert.Equal(1, statefulSet.Spec.UpdateStrategy.RollingUpdate.MaxUnavailable);

                var container = Assert.Single(statefulSet.Spec.Template.Spec.Containers);
                Assert.Contains(container.Ports!, port => port.ContainerPort == 50051 && port.Name == "taskbroker");
                Assert.Contains(container.VolumeMounts!, mount => mount.Name == "config" && mount.MountPath == "/etc/taskbroker");
                Assert.Contains(statefulSet.Spec.Template.Spec.Volumes!, volume => volume.Name == "config" && volume.ConfigMap?.Name == "taskbroker-conf");
            }),
        new(
            "Symbolicator converter",
            typeof(SymbolicatorConverter),
            "symbolicator",
            deployment => TestFixtureData.GetFixtureService("symbolicator", deployment),
            null,
            resources =>
            {
                var deployment = AssertSingleDeployment(resources, "symbolicator");
                var container = Assert.Single(deployment.Spec.Template.Spec.Containers);
                Assert.Contains(container.Ports!, port => port.ContainerPort == 3021 && port.Name == "http");
                Assert.Equal(1001, container.SecurityContext!.RunAsUser);
                Assert.Contains(deployment.Spec.Template.Spec.Volumes!, volume => volume.Name == "symbolicator-conf" && volume.ConfigMap?.Name == "symbolicator-conf");
            }),
        new(
            "Relay converter",
            typeof(RelayConverter),
            "relay",
            deployment => TestFixtureData.GetFixtureService("relay", deployment, service => service.Ports = ["3000:3000"]),
            null,
            resources =>
            {
                AssertResourceTypes(resources, typeof(V1Deployment), typeof(V1Service));
                Assert.Equal(["relay", "relay-service"], resources.Select(resource => resource.Metadata.Name).ToArray());

                var deployment = AssertSingleDeployment(resources, "relay");
                var container = Assert.Single(deployment.Spec.Template.Spec.Containers);
                Assert.Contains(container.Ports!, port => port.ContainerPort == 3000);
                Assert.Contains(container.VolumeMounts!, mount => mount.Name == "geoip" && mount.MountPath == "/geoip");
                Assert.Contains(container.VolumeMounts!, mount => mount.Name == "relay-conf" && mount.MountPath == "/work/.relay");
            }),
        new(
            "GeoIP converter",
            typeof(GeoIPConverter),
            "geoipupdate",
            _ => new DockerService
            {
                Image = "maxmindinc/geoipupdate:latest",
                Volumes = ["geoip:/sentry"],
            },
            null,
            resources =>
            {
                var deployment = AssertSingleDeployment(resources, "geoipupdate");
                var container = Assert.Single(deployment.Spec.Template.Spec.Containers);
                Assert.Equal(["/bin/sh", "-ce"], container.Command);
                Assert.Contains("/usr/bin/geoipupdate", Assert.Single(container.Args!));
                Assert.Contains(container.Env!, env => env.Name == "GEOIPUPDATE_LICENSE_KEY" && env.Value == "test-license");
                Assert.Contains(container.Env!, env => env.Name == "GEOIPUPDATE_ACCOUNT_ID" && env.Value == "test-account");
                Assert.Contains(container.Env!, env => env.Name == "GEOIPUPDATE_EDITION_IDS" && env.Value == "GeoLite2-City");
                Assert.Contains(deployment.Spec.Template.Spec.Volumes!, volume => volume.Name == "geoip-conf" && volume.ConfigMap?.Name == "geoip-conf");
            }),
        new(
            "Memcached converter",
            typeof(MemcachedConverter),
            "memcached",
            deployment => TestFixtureData.GetFixtureService("memcached", deployment),
            null,
            resources =>
            {
                var deployment = AssertSingleDeployment(resources, "memcached");
                var container = Assert.Single(deployment.Spec.Template.Spec.Containers);
                Assert.Null(container.LivenessProbe);
                Assert.Null(container.ReadinessProbe);
            }),
        new(
            "Sentry cleanup converter",
            typeof(SentryCleanupConverter),
            "sentry-cleanup",
            deployment => TestFixtureData.GetFixtureService("sentry-cleanup", deployment),
            null,
            resources =>
            {
                var deployment = AssertSingleDeployment(resources, "sentry-cleanup");
                var container = Assert.Single(deployment.Spec.Template.Spec.Containers);
                Assert.Equal("getsentry/sentry:23.6.1", container.Image);
                Assert.Contains("gosu sentry sentry cleanup --days $SENTRY_EVENT_RETENTION_DAYS", Assert.Single(container.Args!));
                Assert.Contains(deployment.Spec.Template.Spec.Volumes!, volume => volume.Name == "sentry-cron" && volume.ConfigMap?.Name == "sentry-cron");
                Assert.Contains(container.VolumeMounts!, mount => mount.Name == "sentry-cron" && mount.MountPath == "/entrypoint.sh");
            }),
        new(
            "Vroom cleanup converter",
            typeof(VroomCleanupConverter),
            "vroom-cleanup",
            _ => new DockerService
            {
                Image = "vroom-cleanup-self-hosted-local",
                Volumes = ["sentry-vroom:/var/lib/sentry-profiles"],
            },
            null,
            resources =>
            {
                var deployment = AssertSingleDeployment(resources, "vroom-cleanup");
                var container = Assert.Single(deployment.Spec.Template.Spec.Containers);
                Assert.Equal("getsentry/vroom:23.6.1", container.Image);
                Assert.Equal(0, container.SecurityContext!.RunAsUser);
                Assert.Contains("find /var/lib/sentry-profiles -type f -mtime +$SENTRY_EVENT_RETENTION_DAYS -delete", Assert.Single(container.Args!));
            }),
        new(
            "Symbolicator cleanup converter",
            typeof(SymbolicatorCleanupConverter),
            "symbolicator-cleanup",
            deployment => TestFixtureData.GetFixtureService("symbolicator-cleanup", deployment),
            null,
            resources =>
            {
                var deployment = AssertSingleDeployment(resources, "symbolicator-cleanup");
                var container = Assert.Single(deployment.Spec.Template.Spec.Containers);
                Assert.Equal("getsentry/symbolicator:nightly", container.Image);
                Assert.Equal(1001, container.SecurityContext!.RunAsGroup);
                Assert.Contains("gosu symbolicator symbolicator cleanup", Assert.Single(container.Args!));
                Assert.Contains(deployment.Spec.Template.Spec.Volumes!, volume => volume.Name == "symbolicator-conf" && volume.ConfigMap?.Name == "symbolicator-conf");
            }),
    ];

    [Theory]
    [MemberData(nameof(ConverterScenarios))]
    public void Convert_GeneratesExpectedManagedResources(ConverterScenario scenario)
    {
        var sentryDeployment = TestFixtureData.CreateDeployment();
        scenario.ConfigureDeployment?.Invoke(sentryDeployment);
        var service = scenario.CreateService(sentryDeployment);
        var converter = (IDockerContainerConverter)Activator.CreateInstance(scenario.ConverterType)!;

        Assert.True(converter.CanConvert(scenario.ServiceName, service));

        var resources = converter.Convert(scenario.ServiceName, service, sentryDeployment).ToList();

        Assert.NotEmpty(resources);
        Assert.All(resources, resource => AssertManagedLabels(resource, scenario.ServiceName, sentryDeployment.Spec.GetVersion()));
        scenario.AssertResources(resources);
    }

    [Theory]
    [MemberData(nameof(ConverterScenarios))]
    public void Resolver_SelectsExpectedConcreteConverter(ConverterScenario scenario)
    {
        if (!scenario.ResolverShouldSelect)
        {
            return;
        }

        var sentryDeployment = TestFixtureData.CreateDeployment();
        scenario.ConfigureDeployment?.Invoke(sentryDeployment);
        var service = scenario.CreateService(sentryDeployment);
        var resolver = new ContainerConverterResolver(TestFixtureData.CreateConverters());

        var converter = resolver.Resolve(scenario.ServiceName, service);

        Assert.IsType(scenario.ConverterType, converter);
    }

    private static void AssertManagedLabels(
        IKubernetesObject<V1ObjectMeta> resource,
        string serviceName,
        string version)
    {
        Assert.Equal("default", resource.Metadata.NamespaceProperty);
        Assert.Equal(serviceName, resource.Metadata.Labels["app.kubernetes.io/name"]);
        Assert.Equal(serviceName, resource.Metadata.Labels["app.kubernetes.io/instance"]);
        Assert.Equal(version, resource.Metadata.Labels["app.kubernetes.io/version"]);
        Assert.Equal("sentry-operator", resource.Metadata.Labels["app.kubernetes.io/managed-by"]);
    }

    private static void AssertResourceTypes(
        IReadOnlyCollection<IKubernetesObject<V1ObjectMeta>> resources,
        params Type[] expectedTypes)
    {
        Assert.Equal(expectedTypes, resources.Select(resource => resource.GetType()).ToArray());
    }

    private static V1Deployment AssertSingleDeployment(
        IReadOnlyCollection<IKubernetesObject<V1ObjectMeta>> resources,
        string name)
    {
        var deployment = Assert.Single(resources.OfType<V1Deployment>());
        Assert.Equal(name, deployment.Metadata.Name);
        return deployment;
    }

    public sealed record ConverterScenario(
        string Name,
        Type ConverterType,
        string ServiceName,
        Func<SentryDeployment, DockerService> CreateService,
        Action<SentryDeployment>? ConfigureDeployment,
        Action<IReadOnlyList<IKubernetesObject<V1ObjectMeta>>> AssertResources,
        bool ResolverShouldSelect = true)
    {
        public override string ToString() => Name;
    }
}
