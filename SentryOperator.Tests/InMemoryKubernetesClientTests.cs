using System.Diagnostics;
using k8s.Models;
using Microsoft.Extensions.Logging;
using SentryOperator.Tests.TestSupport;

namespace SentryOperator.Tests;

public class InMemoryKubernetesClientTests
{
    private readonly ILoggerFactory _loggerFactory;

    public InMemoryKubernetesClientTests(ITestOutputHelper output)
    {
        _loggerFactory = new TestLoggerFactory(output);
    }

    [Fact]
    public async Task CreateAsync_WorkloadLogsDeploymentLifecycleAndWaitsForReadiness()
    {
        var client = new InMemoryKubernetesClient(_loggerFactory.CreateLogger<InMemoryKubernetesClient>());
        var deployment = new V1Deployment
        {
            Metadata = new V1ObjectMeta
            {
                Name = "test-deployment",
                NamespaceProperty = "default",
            },
            Spec = new V1DeploymentSpec
            {
                Replicas = 1,
            },
        };
        var stopwatch = Stopwatch.StartNew();

        var createTask = client.CreateAsync(deployment);

        await client.WaitForConditionAsync(
            static c => c.Operations.Any(operation => operation.Method == "DeployStart"),
            TimeSpan.FromSeconds(1));

        Assert.False(createTask.IsCompleted);
        Assert.Equal(0, client.GetStored<V1Deployment>("test-deployment")!.Status!.AvailableReplicas);

        await createTask;
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(2));
        Assert.Contains(client.Operations, operation =>
            operation is
            {
                Method: "DeployStart",
                ResourceType: nameof(V1Deployment),
                Name: "test-deployment",
                Detail: "Create",
            });
        Assert.Contains(client.Operations, operation =>
            operation is
            {
                Method: "DeployComplete",
                ResourceType: nameof(V1Deployment),
                Name: "test-deployment",
                Detail: "Create",
            });
        Assert.Equal(1, client.GetStored<V1Deployment>("test-deployment")!.Status!.AvailableReplicas);
    }
}
