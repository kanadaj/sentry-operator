using SentryOperator.Docker.Compose;

namespace SentryOperator.Docker;

public class DeploymentGraph
{
    private readonly IReadOnlyDictionary<string, DockerService> _services;

    public DeploymentGraph(IReadOnlyDictionary<string, DockerService> services)
    {
        _services = services;
    }

    public bool TryCalculateOrder(out List<string> deploymentOrder)
    {
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();
        deploymentOrder = [];

        foreach (var serviceName in _services.Keys)
        {
            if (!Visit(serviceName, visited, visiting, deploymentOrder))
            {
                deploymentOrder = _services.Keys.ToList();
                return false;
            }
        }

        return true;
    }

    public Dictionary<string, ServiceCondition> GetDependencies(string serviceName)
    {
        if (!_services.TryGetValue(serviceName, out var service) || service.DependsOn == null)
        {
            return [];
        }

        return service.DependsOn.ToDictionary(dependency => dependency.Key, dependency => dependency.Value.Condition);
    }

    public List<string> GetDependents(string serviceName)
    {
        return _services
            .Where(service => service.Value.DependsOn?.ContainsKey(serviceName) ?? false)
            .Select(service => service.Key)
            .ToList();
    }

    public List<string> GetRootServices()
    {
        return _services
            .Where(service => service.Value.DependsOn == null || service.Value.DependsOn.Count == 0)
            .Select(service => service.Key)
            .ToList();
    }

    private bool Visit(
        string serviceName,
        HashSet<string> visited,
        HashSet<string> visiting,
        List<string> deploymentOrder)
    {
        if (visiting.Contains(serviceName))
        {
            return false;
        }

        if (visited.Contains(serviceName))
        {
            return true;
        }

        visiting.Add(serviceName);
        if (_services.TryGetValue(serviceName, out var service) && service.DependsOn != null)
        {
            foreach (var dependency in service.DependsOn.Keys)
            {
                if (!Visit(dependency, visited, visiting, deploymentOrder))
                {
                    return false;
                }
            }
        }

        visiting.Remove(serviceName);
        visited.Add(serviceName);
        deploymentOrder.Add(serviceName);
        return true;
    }
}
