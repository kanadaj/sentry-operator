using k8s;
using k8s.Models;
using SentryOperator.Docker.Compose;
using SentryOperator.Extensions;

namespace SentryOperator.Docker;

/// <summary>
/// Orchestrates deployment of services based on their dependencies from docker-compose.
/// Calculates deployment order using topological sort and provides dependency tracking.
/// </summary>
public class DeploymentOrchestrator
{
    private readonly ILogger _logger;
    private readonly DeploymentGraph _deploymentGraph;
    private readonly ServiceResourceMapper _resourceMapper;
    private readonly WorkloadReadinessEvaluator _readinessEvaluator;
    private Dictionary<string, List<IKubernetesObject<V1ObjectMeta>>> _serviceResources = [];
    private List<string> _deploymentOrder = [];

    public DeploymentOrchestrator(ILogger logger, DockerCompose dockerCompose)
        : this(logger, dockerCompose, new SentryManagedServicePolicy())
    {
    }

    public DeploymentOrchestrator(
        ILogger logger,
        DockerCompose dockerCompose,
        IManagedServicePolicy managedServicePolicy)
    {
        _logger = logger;
        _deploymentGraph = new DeploymentGraph(dockerCompose.Services ?? []);
        _resourceMapper = new ServiceResourceMapper();
        _readinessEvaluator = new WorkloadReadinessEvaluator(managedServicePolicy);
    }

    /// <summary>
    /// Maps K8s resources to their source services based on app.kubernetes.io/part-of label.
    /// </summary>
    public void MapResourcesToServices(List<IKubernetesObject<V1ObjectMeta>> resources)
    {
        _serviceResources = _resourceMapper.Map(resources);
    }

    /// <summary>
    /// Calculates the deployment order based on DependsOn relationships using topological sort.
    /// </summary>
    public List<string> CalculateDeploymentOrder()
    {
        var isAcyclic = _deploymentGraph.TryCalculateOrder(out _deploymentOrder);
        if (!isAcyclic)
        {
            _logger.LogWarning("Circular dependency detected in services");
        }

        _logger.LogInformation("Calculated deployment order: {Order}", string.Join(" -> ", _deploymentOrder));
        return _deploymentOrder;
    }

    /// <summary>
    /// Returns each service and its direct dependencies in deployment order.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, ServiceCondition>> GetDependencyMap()
    {
        if (_deploymentOrder.Count == 0)
        {
            CalculateDeploymentOrder();
        }

        return _deploymentOrder.ToDictionary(
            serviceName => serviceName,
            serviceName => (IReadOnlyDictionary<string, ServiceCondition>)GetDependencies(serviceName));
    }

    /// <summary>
    /// Returns resources in deployment order, grouped by service.
    /// </summary>
    public IEnumerable<(string ServiceName, IKubernetesObject<V1ObjectMeta> Resource)> GetOrderedResources()
    {
        foreach (var serviceName in _deploymentOrder)
        {
            if (_serviceResources.ContainsKey(serviceName))
            {
                foreach (var resource in _serviceResources[serviceName])
                {
                    yield return (serviceName, resource);
                }
            }
        }
    }

    /// <summary>
    /// Gets dependencies for a specific service.
    /// </summary>
    public Dictionary<string, ServiceCondition> GetDependencies(string serviceName)
    {
        return _deploymentGraph.GetDependencies(serviceName);
    }

    /// <summary>
    /// Gets all services that depend on the given service.
    /// </summary>
    public List<string> GetDependents(string serviceName)
    {
        return _deploymentGraph.GetDependents(serviceName);
    }

    /// <summary>
    /// Adds dependency information as labels to resources.
    /// </summary>
    public void AddDependencyLabels(IKubernetesObject<V1ObjectMeta> resource, string serviceName)
    {
        var dependencies = GetDependencies(serviceName);
        if (dependencies.Count == 0)
        {
            resource.SetLabel("sentry-operator/dependencies", "none");
        }
        else
        {
            var depString = string.Join(",", dependencies.Select(d => $"{d.Key}:{d.Value}"));
            resource.SetLabel("sentry-operator/dependencies", depString);
        }

        var dependents = GetDependents(serviceName);
        if (dependents.Count > 0)
        {
            resource.SetLabel("sentry-operator/dependents", string.Join(",", dependents));
        }
    }

    /// <summary>
    /// Gets all services with no dependencies (can be deployed first).
    /// </summary>
    public List<string> GetRootServices()
    {
        return _deploymentGraph.GetRootServices();
    }

    /// <summary>
    /// Checks if all dependencies of a service are satisfied based on actual deployment status.
    /// </summary>
    public bool AreDependenciesSatisfied(
        string serviceName,
        IList<V1Deployment> actualDeployments,
        IList<V1StatefulSet> actualStatefulSets)
    {
        return _readinessEvaluator.AreDependenciesSatisfied(
            GetDependencies(serviceName),
            actualDeployments,
            actualStatefulSets);
    }
}
