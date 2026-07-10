using Microsoft.Extensions.Logging.Abstractions;
using SentryOperator.Docker;
using SentryOperator.Docker.Compose;
using Xunit.Abstractions;

namespace SentryOperator.Tests;

public class DeploymentOrchestratorTests
{
    private readonly ITestOutputHelper _output;

    public DeploymentOrchestratorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CurrentComposeSnapshot_ProducesDependencyMapWithValidDeploymentOrder()
    {
        var dockerCompose = ParseCurrentComposeSnapshot();
        var orchestrator = new DeploymentOrchestrator(
            NullLogger<DeploymentOrchestrator>.Instance,
            dockerCompose);

        var dependencyMap = orchestrator.GetDependencyMap();
        var deploymentOrder = dependencyMap.Keys.ToList();
        var indexByService = deploymentOrder
            .Select((service, index) => (service, index))
            .ToDictionary(item => item.service, item => item.index);

        Assert.NotEmpty(dependencyMap);
        Assert.Equal(dockerCompose.Services!.Count, dependencyMap.Count);
        Assert.Contains(dependencyMap, item => item.Value.Count > 0);

        foreach (var (serviceName, dependencies) in dependencyMap)
        {
            foreach (var dependencyName in dependencies.Keys)
            {
                Assert.True(
                    indexByService.ContainsKey(dependencyName),
                    $"{serviceName} depends on unknown service {dependencyName}.");
                Assert.True(
                    indexByService[dependencyName] < indexByService[serviceName],
                    $"{dependencyName} must be deployed before {serviceName}.");
            }
        }

        PrintDependencyMap(dependencyMap);
    }

    [Fact]
    public void DependencyMap_PreservesDependencyConditionsAndTransitiveOrder()
    {
        var dockerCompose = new DockerCompose
        {
            Services = new Dictionary<string, DockerService>
            {
                ["web"] = new()
                {
                    DependsOn = new Dictionary<string, DependsOn>
                    {
                        ["api"] = new() { Condition = ServiceCondition.ServiceHealthy },
                        ["database"] = new() { Condition = ServiceCondition.ServiceStarted },
                    },
                },
                ["api"] = new()
                {
                    DependsOn = new Dictionary<string, DependsOn>
                    {
                        ["database"] = new() { Condition = ServiceCondition.ServiceHealthy },
                    },
                },
                ["database"] = new(),
            },
        };
        var orchestrator = new DeploymentOrchestrator(
            NullLogger<DeploymentOrchestrator>.Instance,
            dockerCompose);

        var dependencyMap = orchestrator.GetDependencyMap();
        var deploymentOrder = dependencyMap.Keys.ToList();

        Assert.Equal(ServiceCondition.ServiceHealthy, dependencyMap["web"]["api"]);
        Assert.Equal(ServiceCondition.ServiceStarted, dependencyMap["web"]["database"]);
        Assert.Empty(dependencyMap["database"]);
        Assert.True(deploymentOrder.IndexOf("database") < deploymentOrder.IndexOf("api"));
        Assert.True(deploymentOrder.IndexOf("api") < deploymentOrder.IndexOf("web"));
    }

    [Fact]
    public void AreDependenciesSatisfied_IgnoresExternallyManagedServices()
    {
        var dockerCompose = new DockerCompose
        {
            Services = new Dictionary<string, DockerService>
            {
                ["web"] = new()
                {
                    DependsOn = new Dictionary<string, DependsOn>
                    {
                        ["redis"] = new() { Condition = ServiceCondition.ServiceHealthy },
                    },
                },
                ["redis"] = new(),
            },
        };
        var orchestrator = new DeploymentOrchestrator(
            NullLogger<DeploymentOrchestrator>.Instance,
            dockerCompose);

        var dependenciesSatisfied = orchestrator.AreDependenciesSatisfied(
            "web",
            Array.Empty<k8s.Models.V1Deployment>(),
            Array.Empty<k8s.Models.V1StatefulSet>());

        Assert.True(dependenciesSatisfied);
    }

    private static DockerCompose ParseCurrentComposeSnapshot()
    {
        var composePath = Path.Combine(AppContext.BaseDirectory, "TestData", "docker-compose.yml");
        var composeYaml = File.ReadAllText(composePath);
        var converter = new DockerComposeConverter(
            NullLogger<DockerComposeConverter>.Instance,
            Array.Empty<Docker.Converters.IDockerContainerConverter>());

        return converter.Parse(composeYaml, overrides: null);
    }

    private void PrintDependencyMap(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ServiceCondition>> dependencyMap)
    {
        _output.WriteLine("Deployment dependency map (in deployment order):");

        var order = 1;
        foreach (var (serviceName, dependencies) in dependencyMap)
        {
            var formattedDependencies = dependencies.Count == 0
                ? "none"
                : string.Join(", ", dependencies.Select(dependency =>
                    $"{dependency.Key} ({dependency.Value})"));

            _output.WriteLine($"{order++,2}. {serviceName} <- {formattedDependencies}");
        }
    }
}
