using k8s;
using k8s.Models;
using KubeOps.Abstractions.Entities;
using Microsoft.Extensions.Logging;
using SentryOperator.Docker;
using SentryOperator.Entities;
using SentryOperator.Extensions;
using SentryOperator.Tests.TestSupport;

namespace SentryOperator.Tests;

public class SentryDeploymentControllerReconcileTests
{
    private readonly ILoggerFactory _loggerFactory;

    public SentryDeploymentControllerReconcileTests(ITestOutputHelper output)
    {
        _loggerFactory = new TestLoggerFactory(output);
    }

    [Fact]
    public async Task ReconcileAsync_CreatesExpectedResourcesInDependencyOrder_AndIsIdempotent()
    {
        var deployment = TestFixtureData.CreateDeployment(entity =>
        {
            entity.Spec.Replicas = new Dictionary<string, int>
            {
                ["taskbroker"] = 2,
                ["taskworker"] = 7,
            };
            entity.Spec.Config!.TaskWorkerConcurrency = 8;
            entity.Spec.Config.TaskWorkerAutoscaling = new TaskWorkerAutoscaling
            {
                MinReplicas = 2,
                MaxReplicas = 8,
                CpuTargetUtilization = 65,
                ConsumerLagTarget = 250,
                ExternalMetricName = "kafka_taskworker_lag",
            };
        });
        var client = new InMemoryKubernetesClient(_loggerFactory.CreateLogger<InMemoryKubernetesClient>());
        client.Seed(deployment);
        var controller = TestFixtureData.CreateController(client, deployment);
        var conversionResult = TestFixtureData.CreateDockerComposeConverter()
            .ConvertWithOrchestrator(TestFixtureData.GetResolvedComposeYaml(deployment), deployment);

        await controller.ReconcileAsync(deployment, CancellationToken.None);
        await client.WaitForConditionAsync(
            static c => c.GetStored<V1ConfigMap>("relay-conf")?.Data?.ContainsKey("credentials.json") == true,
            TimeSpan.FromSeconds(10));

        var storedDeployment = client.GetStored<SentryDeployment>(deployment.Metadata.Name!)!;
        Assert.Equal("Ready", storedDeployment.Status.Status);
        Assert.Equal("Sentry deployment is ready", storedDeployment.Status.Message);
        Assert.Equal(conversionResult.Resources.GetChecksum(), storedDeployment.Status.LastVersion);

        AssertGeneratedResourcesMatchExpected(client, deployment, conversionResult.Resources);
        AssertSupplementaryResources(client, deployment);
        AssertDependencySafeOperationOrder(client, conversionResult.Orchestrator);
        AssertWorkloadsDeployInParallel(client);
        Assert.All(client.ListStored<V1Deployment>().Where(IsManaged), item => Assert.True((item.Status?.AvailableReplicas ?? 0) > 0));
        Assert.All(client.ListStored<V1StatefulSet>().Where(IsManaged), item => Assert.True((item.Status?.ReadyReplicas ?? 0) > 0));

        var mutationCount = client.MutationOperations.Count;
        var snapshot = client.ExportResourceFingerprints();

        await controller.ReconcileAsync(storedDeployment, CancellationToken.None);

        var additionalMutations = client.MutationOperations.Skip(mutationCount).ToArray();
        Assert.True(
            client.MutationOperations.Count == mutationCount,
            $"Unexpected mutations after second reconcile: {string.Join(", ", additionalMutations.Select(operation => $"{operation.Method}:{operation.ResourceType}/{operation.Name}"))}");
        Assert.Equal(snapshot, client.ExportResourceFingerprints());
    }

    [Fact]
    public async Task ReconcileAsync_WithoutTaskworkerAutoscaling_DoesNotCreateHorizontalPodAutoscaler()
    {
        var deployment = TestFixtureData.CreateDeployment(entity =>
        {
            entity.Spec.Replicas = new Dictionary<string, int>
            {
                ["taskbroker"] = 2,
                ["taskworker"] = 3,
            };
        });
        var client = new InMemoryKubernetesClient(_loggerFactory.CreateLogger<InMemoryKubernetesClient>());
        client.Seed(deployment);
        var controller = TestFixtureData.CreateController(client, deployment);
        var expectedResources = TestFixtureData.CreateDockerComposeConverter()
            .Convert(TestFixtureData.GetResolvedComposeYaml(deployment), deployment);

        await controller.ReconcileAsync(deployment, CancellationToken.None);
        await client.WaitForConditionAsync(
            static c => c.GetStored<V1ConfigMap>("relay-conf")?.Data?.ContainsKey("credentials.json") == true,
            TimeSpan.FromSeconds(10));

        Assert.Empty(client.ListStored<V2HorizontalPodAutoscaler>());
        Assert.Equal(expectedResources.GetChecksum(), client.GetStored<SentryDeployment>(deployment.Metadata.Name!)!.Status.LastVersion);
        Assert.Equal(3, client.GetStored<V1Deployment>("taskworker")!.Spec.Replicas);
    }

    private static void AssertGeneratedResourcesMatchExpected(
        InMemoryKubernetesClient client,
        SentryDeployment owner,
        IEnumerable<IKubernetesObject<V1ObjectMeta>> expectedResources)
    {
        var expectedChecksums = BuildExpectedChecksumLabels(owner, expectedResources);

        Assert.Equal(expectedResources.OfType<V1Service>().Count(), client.ListStored<V1Service>().Count(IsManaged));
        Assert.Equal(expectedResources.OfType<V1Deployment>().Count(), client.ListStored<V1Deployment>().Count(IsManaged));
        Assert.Equal(expectedResources.OfType<V1StatefulSet>().Count(), client.ListStored<V1StatefulSet>().Count(IsManaged));
        Assert.Equal(expectedResources.OfType<V2HorizontalPodAutoscaler>().Count(), client.ListStored<V2HorizontalPodAutoscaler>().Count(IsManaged));

        foreach (var expected in expectedResources)
        {
            switch (expected)
            {
                case V1Service:
                    AssertManagedResource(
                        client.GetStored<V1Service>(expected.Metadata.Name!)!,
                        expected,
                        owner,
                        expectedChecksums[CreateResourceKey(expected)]);
                    break;
                case V1Deployment:
                    AssertManagedResource(
                        client.GetStored<V1Deployment>(expected.Metadata.Name!)!,
                        expected,
                        owner,
                        expectedChecksums[CreateResourceKey(expected)]);
                    break;
                case V1StatefulSet:
                    AssertManagedResource(
                        client.GetStored<V1StatefulSet>(expected.Metadata.Name!)!,
                        expected,
                        owner,
                        expectedChecksums[CreateResourceKey(expected)]);
                    break;
                case V2HorizontalPodAutoscaler:
                    AssertManagedResource(
                        client.GetStored<V2HorizontalPodAutoscaler>(expected.Metadata.Name!)!,
                        expected,
                        owner,
                        expectedChecksums[CreateResourceKey(expected)]);
                    break;
            }
        }
    }

    private static void AssertManagedResource(
        IKubernetesObject<V1ObjectMeta> actual,
        IKubernetesObject<V1ObjectMeta> expected,
        SentryDeployment owner,
        string expectedChecksum)
    {
        Assert.Equal(expected.Metadata.Name, actual.Metadata.Name);
        Assert.Equal(expected.Metadata.NamespaceProperty, actual.Metadata.NamespaceProperty);
        Assert.Equal(expected.Metadata.Labels["app.kubernetes.io/name"], actual.Metadata.Labels["app.kubernetes.io/name"]);
        Assert.Equal("sentry-operator", actual.Metadata.Labels["app.kubernetes.io/managed-by"]);
        Assert.True(
            expectedChecksum == actual.Metadata.Labels["sentry-operator/checksum"],
            $"{actual.GetType().Name}/{actual.Metadata.Name} expected checksum {expectedChecksum} but found {actual.Metadata.Labels["sentry-operator/checksum"]}.");

        var ownerReference = Assert.Single(actual.Metadata.OwnerReferences!);
        Assert.Equal(owner.Metadata.Name, ownerReference.Name);
        Assert.Equal(owner.Metadata.Uid, ownerReference.Uid);
        Assert.Equal("SentryDeployment", ownerReference.Kind);
    }

    private static Dictionary<string, string> BuildExpectedChecksumLabels(
        SentryDeployment owner,
        IEnumerable<IKubernetesObject<V1ObjectMeta>> expectedResources)
    {
        var services = expectedResources.OfType<V1Service>()
            .Select(resource => KubernetesYaml.Deserialize<V1Service>(KubernetesYaml.Serialize(resource), strict: false))
            .ToList();
        var deployments = expectedResources.OfType<V1Deployment>()
            .Select(resource => KubernetesYaml.Deserialize<V1Deployment>(KubernetesYaml.Serialize(resource), strict: false))
            .ToList();
        var statefulSets = expectedResources.OfType<V1StatefulSet>()
            .Select(resource => KubernetesYaml.Deserialize<V1StatefulSet>(KubernetesYaml.Serialize(resource), strict: false))
            .ToList();

        if (services.Count > 0)
        {
            services[0].AddOwnerReference(owner.MakeOwnerReference());
        }
        else if (deployments.Count > 0)
        {
            deployments[0].AddOwnerReference(owner.MakeOwnerReference());
        }
        else if (statefulSets.Count > 0)
        {
            statefulSets[0].AddOwnerReference(owner.MakeOwnerReference());
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var service in services)
        {
            result[CreateResourceKey(service)] = service.GetChecksum();
        }

        foreach (var deployment in deployments)
        {
            result[CreateResourceKey(deployment)] = deployment.GetChecksum();
        }

        foreach (var statefulSet in statefulSets)
        {
            result[CreateResourceKey(statefulSet)] = statefulSet.GetChecksum();
        }

        foreach (var hpa in expectedResources.OfType<V2HorizontalPodAutoscaler>())
        {
            result[CreateResourceKey(hpa)] = hpa.GetChecksum();
        }

        return result;
    }

    private static void AssertSupplementaryResources(
        InMemoryKubernetesClient client,
        SentryDeployment owner)
    {
        Assert.NotNull(client.GetStored<V1Secret>("sentry-env"));
        Assert.NotNull(client.GetStored<V1Secret>("sentry-config"));
        Assert.NotNull(client.GetStored<V1ConfigMap>("sentry-cron"));
        Assert.NotNull(client.GetStored<V1ConfigMap>("snuba-env"));
        Assert.NotNull(client.GetStored<V1ConfigMap>("snuba-healthcheck"));

        var relayConfig = client.GetStored<V1ConfigMap>("relay-conf");
        Assert.NotNull(relayConfig);
        Assert.Contains("credentials.json", relayConfig!.Data.Keys);

        var certificate = client.GetStored<Certificate>($"{owner.Metadata.Name}-certificate");
        Assert.NotNull(certificate);
        var ownerReference = Assert.Single(certificate!.Metadata.OwnerReferences!);
        Assert.Equal(owner.Metadata.Name, ownerReference.Name);
    }

    private static void AssertDependencySafeOperationOrder(
        InMemoryKubernetesClient client,
        DeploymentOrchestrator orchestrator)
    {
        var serviceCreateOperations = client.Operations
            .Where(operation => operation.Method == "Create" &&
                                operation.ResourceType == nameof(V1Service))
            .Select(operation => operation.Sequence)
            .ToArray();
        var workloadCreateOperations = client.Operations
            .Where(operation => operation.Method == "Create" &&
                                operation.ResourceType is nameof(V1Deployment) or nameof(V1StatefulSet))
            .ToArray();

        if (serviceCreateOperations.Length > 0)
        {
            var firstWorkloadCreate = workloadCreateOperations.Min(operation => operation.Sequence);
            Assert.All(serviceCreateOperations, sequence => Assert.True(sequence < firstWorkloadCreate));
        }

        var workloadCreateSequence = workloadCreateOperations.ToDictionary(operation => operation.Name, operation => operation.Sequence);
        foreach (var (serviceName, dependencies) in orchestrator.GetDependencyMap())
        {
            if (!workloadCreateSequence.TryGetValue(serviceName, out var serviceSequence))
            {
                continue;
            }

            foreach (var dependencyName in dependencies.Keys)
            {
                if (!workloadCreateSequence.TryGetValue(dependencyName, out var dependencySequence))
                {
                    continue;
                }

                Assert.True(
                    dependencySequence < serviceSequence,
                    $"{dependencyName} should be created before {serviceName}.");
            }
        }
    }

    private static void AssertWorkloadsDeployInParallel(InMemoryKubernetesClient client)
    {
        var firstCompletionSequence = client.Operations
            .Where(operation => operation.Method == "DeployComplete")
            .Min(operation => operation.Sequence);
        var startedBeforeFirstCompletion = client.Operations
            .Where(operation => operation.Method == "DeployStart" &&
                                operation.Sequence < firstCompletionSequence)
            .ToArray();

        Assert.True(
            startedBeforeFirstCompletion.Length > 1,
            "Expected multiple dependency-ready workloads to start before the first deployment completed.");
    }

    private static bool IsManaged(IKubernetesObject<V1ObjectMeta> resource) =>
        resource.Metadata.Labels?.TryGetValue("app.kubernetes.io/managed-by", out var managedBy) == true &&
        managedBy == "sentry-operator";

    private static string CreateResourceKey(IKubernetesObject<V1ObjectMeta> resource) =>
        $"{resource.GetType().Name}/{resource.Metadata.Name}";
}
