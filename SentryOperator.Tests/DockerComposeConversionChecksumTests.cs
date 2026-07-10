using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using SentryOperator.Docker;
using SentryOperator.Docker.Converters;
using SentryOperator.Entities;
using SentryOperator.Extensions;

namespace SentryOperator.Tests;

public class DockerComposeConversionChecksumTests
{
    [Fact]
    public void CurrentComposeSnapshot_ProducesStableGeneratedResourceChecksums()
    {
        var composeYaml = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "TestData", "docker-compose.yml"));
        var deployment = CreateDeployment();
        var converter = new DockerComposeConverter(
            NullLogger<DockerComposeConverter>.Instance,
            CreateConverters());

        var resources = converter.Convert(
            deployment.Spec.Config!.ReplaceVariables(composeYaml, deployment.Spec.GetVersion()),
            deployment);
        Assert.NotEmpty(resources);
        Assert.All(resources, resource => Assert.False(string.IsNullOrWhiteSpace(resource.GetChecksum())));
        Assert.Equal(ExpectedCombinedChecksum, resources.GetChecksum());
    }

    [Fact]
    public void TaskworkerAutoscaling_GeneratesHpaAndUsesMinimumDeploymentReplicas()
    {
        var deployment = CreateDeployment();
        deployment.Spec.Replicas = new Dictionary<string, int> { ["taskworker"] = 7 };
        deployment.Spec.Config!.TaskWorkerAutoscaling = new TaskWorkerAutoscaling
        {
            MinReplicas = 2,
            MaxReplicas = 8,
            CpuTargetUtilization = 65,
            ConsumerLagTarget = 250,
            ExternalMetricName = "kafka_taskworker_lag",
        };

        var resources = Convert(deployment);
        var taskworker = Assert.Single(resources.OfType<V1Deployment>(), item => item.Name() == "taskworker");
        var hpa = Assert.Single(resources.OfType<V2HorizontalPodAutoscaler>());

        Assert.Equal(2, taskworker.Spec.Replicas);
        Assert.Equal("taskworker-autoscaler", hpa.Name());
        Assert.Equal("apps/v1", hpa.Spec.ScaleTargetRef.ApiVersion);
        Assert.Equal("Deployment", hpa.Spec.ScaleTargetRef.Kind);
        Assert.Equal("taskworker", hpa.Spec.ScaleTargetRef.Name);
        Assert.Equal(2, hpa.Spec.MinReplicas);
        Assert.Equal(8, hpa.Spec.MaxReplicas);

        var cpuMetric = Assert.Single(hpa.Spec.Metrics, item => item.Type == "Resource");
        Assert.Equal("cpu", cpuMetric.Resource.Name);
        Assert.Equal("Utilization", cpuMetric.Resource.Target.Type);
        Assert.Equal(65, cpuMetric.Resource.Target.AverageUtilization);

        var lagMetric = Assert.Single(hpa.Spec.Metrics, item => item.Type == "External");
        Assert.Equal("kafka_taskworker_lag", lagMetric.External.Metric.Name);
        Assert.Equal("AverageValue", lagMetric.External.Target.Type);
        Assert.Equal("250", lagMetric.External.Target.AverageValue.ToString());
    }

    [Fact]
    public void TaskworkerWithoutAutoscaling_PreservesFixedReplicaBehavior()
    {
        var deployment = CreateDeployment();
        deployment.Spec.Replicas = new Dictionary<string, int> { ["taskworker"] = 3 };

        var resources = Convert(deployment);

        var taskworker = Assert.Single(resources.OfType<V1Deployment>(), item => item.Name() == "taskworker");
        Assert.Equal(3, taskworker.Spec.Replicas);
        Assert.Empty(resources.OfType<V2HorizontalPodAutoscaler>());
    }

    [Theory]
    [InlineData(0, 1, 80, 100, "metric")]
    [InlineData(2, 1, 80, 100, "metric")]
    [InlineData(1, 2, 0, 100, "metric")]
    [InlineData(1, 2, 101, 100, "metric")]
    [InlineData(1, 2, 80, 0, "metric")]
    [InlineData(1, 2, 80, 100, " ")]
    public void TaskworkerAutoscaling_InvalidSettingsAreRejected(int minReplicas, int maxReplicas,
        int cpuTargetUtilization, int consumerLagTarget, string externalMetricName)
    {
        var autoscaling = new TaskWorkerAutoscaling
        {
            MinReplicas = minReplicas,
            MaxReplicas = maxReplicas,
            CpuTargetUtilization = cpuTargetUtilization,
            ConsumerLagTarget = consumerLagTarget,
            ExternalMetricName = externalMetricName,
        };

        Assert.False(string.IsNullOrWhiteSpace(autoscaling.Validate()));
    }

    [Fact]
    public void TaskworkerAutoscaling_DefaultSettingsAreValid()
    {
        var autoscaling = new TaskWorkerAutoscaling();

        Assert.Null(autoscaling.Validate());
        Assert.Equal(TaskWorkerAutoscaling.DefaultExternalMetricName, autoscaling.ExternalMetricName);
    }

    private static IList<k8s.IKubernetesObject<V1ObjectMeta>> Convert(SentryDeployment deployment)
    {
        var composeYaml = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "TestData", "docker-compose.yml"));
        var converter = new DockerComposeConverter(
            NullLogger<DockerComposeConverter>.Instance,
            CreateConverters());
        return converter.Convert(
            deployment.Spec.Config!.ReplaceVariables(composeYaml, deployment.Spec.GetVersion()),
            deployment);
    }

    private static IEnumerable<IDockerContainerConverter> CreateConverters()
    {
        return typeof(IDockerContainerConverter).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract &&
                           typeof(IDockerContainerConverter).IsAssignableFrom(type))
            .Select(type => (IDockerContainerConverter)Activator.CreateInstance(type)!);
    }

    private static SentryDeployment CreateDeployment()
    {
        return new SentryDeployment
        {
            Metadata = new V1ObjectMeta
            {
                Name = "sentry",
                NamespaceProperty = "default",
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
        };
    }

    private const string ExpectedCombinedChecksum = "AD35C6B80DAB1C46A21AA3D1D90CD645";
}
