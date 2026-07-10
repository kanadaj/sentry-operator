using System.Collections;
using k8s;
using k8s.Models;
using SentryOperator.Entities;

namespace SentryOperator.Docker;

/// <summary>
/// Orchestrates deployment of services based on their dependencies from docker-compose.
/// Calculates deployment order using topological sort and provides dependency tracking.
/// </summary>
public class DeploymentOrchestrator
{
    private readonly ILogger _logger;
    private readonly Dictionary<string, DockerService> _services;
    private readonly Dictionary<string, List<IKubernetesObject<V1ObjectMeta>>> _serviceResources;
    private List<string> _deploymentOrder;

    public DeploymentOrchestrator(ILogger logger, DockerCompose dockerCompose)
    {
        _logger = logger;
        _services = dockerCompose.Services ?? new Dictionary<string, DockerService>();
        _serviceResources = new Dictionary<string, List<IKubernetesObject<V1ObjectMeta>>>();
        _deploymentOrder = new List<string>();
    }

    /// <summary>
    /// Maps K8s resources to their source services based on app.kubernetes.io/part-of label.
    /// </summary>
    public void MapResourcesToServices(List<IKubernetesObject<V1ObjectMeta>> resources)
    {
        foreach (var resource in resources)
        {
            var partOf = GetLabel(resource, "app.kubernetes.io/part-of") ?? GetLabel(resource, "app.kubernetes.io/name");
            if (!string.IsNullOrEmpty(partOf))
            {
                if (!_serviceResources.ContainsKey(partOf))
                {
                    _serviceResources[partOf] = new List<IKubernetesObject<V1ObjectMeta>>();
                }
                _serviceResources[partOf].Add(resource);
            }
        }
    }

    /// <summary>
    /// Calculates the deployment order based on DependsOn relationships using topological sort.
    /// </summary>
    public List<string> CalculateDeploymentOrder()
    {
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();
        var result = new List<string>();

        foreach (var serviceName in _services.Keys)
        {
            if (!visited.Contains(serviceName))
            {
                if (!TopologicalSort(serviceName, visited, visiting, result))
                {
                    _logger.LogWarning("Circular dependency detected in services");
                    return _services.Keys.ToList(); // Fallback to unordered
                }
            }
        }

        _deploymentOrder = result;
        _logger.LogInformation("Calculated deployment order: {Order}", string.Join(" -> ", result));
        return result;
    }

    /// <summary>
    /// Depth-first search for topological sorting with cycle detection.
    /// </summary>
    private bool TopologicalSort(string serviceName, HashSet<string> visited, HashSet<string> visiting, List<string> result)
    {
        if (visiting.Contains(serviceName))
        {
            return false; // Cycle detected
        }

        if (visited.Contains(serviceName))
        {
            return true; // Already processed
        }

        visiting.Add(serviceName);

        var service = _services.GetValueOrDefault(serviceName);
        if (service?.DependsOn != null)
        {
            foreach (var dependency in service.DependsOn.Keys)
            {
                if (!TopologicalSort(dependency, visited, visiting, result))
                {
                    return false;
                }
            }
        }

        visiting.Remove(serviceName);
        visited.Add(serviceName);
        result.Add(serviceName);
        return true;
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
        if (_services.TryGetValue(serviceName, out var service) && service.DependsOn != null)
        {
            var result = new Dictionary<string, ServiceCondition>();
            foreach (var dep in service.DependsOn)
            {
                result[dep.Key] = dep.Value.Condition;
            }
            return result;
        }
        return new Dictionary<string, ServiceCondition>();
    }

    /// <summary>
    /// Gets all services that depend on the given service.
    /// </summary>
    public List<string> GetDependents(string serviceName)
    {
        var dependents = new List<string>();
        foreach (var (name, service) in _services)
        {
            if (service.DependsOn?.ContainsKey(serviceName) ?? false)
            {
                dependents.Add(name);
            }
        }
        return dependents;
    }

    /// <summary>
    /// Adds dependency information as labels to resources.
    /// </summary>
    public void AddDependencyLabels(IKubernetesObject<V1ObjectMeta> resource, string serviceName)
    {
        var dependencies = GetDependencies(serviceName);
        if (dependencies.Count == 0)
        {
            SetLabel(resource, "sentry-operator/dependencies", "none");
        }
        else
        {
            var depString = string.Join(",", dependencies.Select(d => $"{d.Key}:{d.Value}"));
            SetLabel(resource, "sentry-operator/dependencies", depString);
        }

        var dependents = GetDependents(serviceName);
        if (dependents.Count > 0)
        {
            SetLabel(resource, "sentry-operator/dependents", string.Join(",", dependents));
        }
    }

    /// <summary>
    /// Gets all services with no dependencies (can be deployed first).
    /// </summary>
    public List<string> GetRootServices()
    {
        return _services
            .Where(s => s.Value.DependsOn == null || s.Value.DependsOn.Count == 0)
            .Select(s => s.Key)
            .ToList();
    }

    /// <summary>
    /// Checks if all dependencies of a service are satisfied based on actual deployment status.
    /// </summary>
    public bool AreDependenciesSatisfied(
        string serviceName,
        IList<V1Deployment> actualDeployments,
        IList<V1StatefulSet> actualStatefulSets)
    {
        var dependencies = GetDependencies(serviceName);
        foreach (var (depName, condition) in dependencies)
        {
            var depDeployment = actualDeployments.FirstOrDefault(d => d.Name() == depName);
            var depStatefulSet = actualStatefulSets.FirstOrDefault(s => s.Name() == depName);

            if (depDeployment == null && depStatefulSet == null)
            {
                return false;
            }

            var deploymentReady = (depDeployment?.Status?.AvailableReplicas ?? 0) > 0;
            var statefulSetReady = (depStatefulSet?.Status?.ReadyReplicas ?? 0) > 0;

            var isReady = condition switch
            {
                ServiceCondition.ServiceStarted => deploymentReady || statefulSetReady,
                ServiceCondition.ServiceHealthy => (depDeployment?.Status?.Conditions?.Any(c => 
                                                        c.Type == "Available" && c.Status == "True") ?? false) ||
                                                    (depStatefulSet?.Status?.Conditions?.Any(c => 
                                                        c.Type == "Available" && c.Status == "True") ?? false),
                ServiceCondition.ServiceCompletedSuccessfully => ((depDeployment?.Status?.Replicas ?? 0) == 0 &&
                                                                  depDeployment?.Metadata?.DeletionTimestamp != null) ||
                                                                 ((depStatefulSet?.Status?.Replicas ?? 0) == 0 &&
                                                                  depStatefulSet?.Metadata?.DeletionTimestamp != null),
                _ => false
            };

            if (!isReady)
            {
                _logger.LogDebug(
                    "Dependency {DependencyName} of {ServiceName} not yet satisfied (condition: {Condition})",
                    depName, serviceName, condition);
                return false;
            }
        }

        return true;
    }

    private string? GetLabel(IKubernetesObject<V1ObjectMeta> resource, string label)
    {
        if (resource?.Metadata?.Labels != null && resource.Metadata.Labels.TryGetValue(label, out var value))
        {
            return value;
        }
        return null;
    }

    private void SetLabel(IKubernetesObject<V1ObjectMeta> resource, string label, string value)
    {
        if (resource?.Metadata == null)
            return;

        resource.Metadata.Labels ??= new Dictionary<string, string>();
        resource.Metadata.Labels[label] = value;
    }
}
