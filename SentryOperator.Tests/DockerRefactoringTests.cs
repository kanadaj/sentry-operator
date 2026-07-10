using k8s;
using k8s.Models;
using SentryOperator.Docker;
using SentryOperator.Docker.Compose;
using SentryOperator.Docker.Converters;
using SentryOperator.Entities;

namespace SentryOperator.Tests;

public class DockerRefactoringTests
{
    [Fact]
    public void Parser_AppliesServiceOverridesWithoutChangingOtherServices()
    {
        const string compose = """
            services:
              web:
                image: sentry:old
              worker:
                image: sentry:worker
            """;
        const string overrides = """
            services:
              web:
                image: sentry:new
            """;

        var result = new YamlDockerComposeParser().Parse(compose, overrides);

        Assert.Equal("sentry:new", result.Services!["web"].Image);
        Assert.Equal("sentry:worker", result.Services["worker"].Image);
    }

    [Fact]
    public void ConverterResolver_SelectsHighestPriorityMatchingConverter()
    {
        var lowPriorityConverter = new TestConverter(priority: 1, canConvert: true);
        var highPriorityConverter = new TestConverter(priority: 2, canConvert: true);
        var resolver = new ContainerConverterResolver([lowPriorityConverter, highPriorityConverter]);

        var result = resolver.Resolve("web", new DockerService());

        Assert.Same(highPriorityConverter, result);
    }

    [Fact]
    public void ManagedServicePolicy_IdentifiesExternalDependencies()
    {
        var policy = new SentryManagedServicePolicy();

        Assert.True(policy.IsExternallyManaged("redis"));
        Assert.False(policy.IsExternallyManaged("web"));
    }

    [Fact]
    public void DeploymentGraph_ReturnsFallbackOrderForCycles()
    {
        var graph = new DeploymentGraph(new Dictionary<string, DockerService>
        {
            ["web"] = new()
            {
                DependsOn = new Dictionary<string, DependsOn>
                {
                    ["worker"] = new(),
                },
            },
            ["worker"] = new()
            {
                DependsOn = new Dictionary<string, DependsOn>
                {
                    ["web"] = new(),
                },
            },
        });

        var isAcyclic = graph.TryCalculateOrder(out var order);

        Assert.False(isAcyclic);
        Assert.Equal(["web", "worker"], order);
    }

    private sealed class TestConverter : IDockerContainerConverter
    {
        private readonly bool _canConvert;

        public TestConverter(int priority, bool canConvert)
        {
            Priority = priority;
            _canConvert = canConvert;
        }

        public int Priority { get; }

        public bool CanConvert(string name, DockerService service) => _canConvert;

        public IEnumerable<IKubernetesObject<V1ObjectMeta>> Convert(
            string name,
            DockerService service,
            SentryDeployment sentryDeployment) =>
            [];

        public bool TryConvert(
            string name,
            DockerService service,
            SentryDeployment sentryDeployment,
            out IKubernetesObject<V1ObjectMeta>[]? resource)
        {
            resource = _canConvert ? [] : null;
            return _canConvert;
        }
    }
}
