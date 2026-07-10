using k8s.Models;
using SentryOperator.Docker.Compose;
using SentryOperator.Extensions;

namespace SentryOperator.Docker;

public class WorkloadReadinessEvaluator
{
    private readonly IManagedServicePolicy _managedServicePolicy;

    public WorkloadReadinessEvaluator(IManagedServicePolicy managedServicePolicy)
    {
        _managedServicePolicy = managedServicePolicy;
    }

    public bool AreDependenciesSatisfied(
        IReadOnlyDictionary<string, ServiceCondition> dependencies,
        IList<V1Deployment> deployments,
        IList<V1StatefulSet> statefulSets)
    {
        foreach (var (dependencyName, condition) in dependencies)
        {
            if (_managedServicePolicy.IsExternallyManaged(dependencyName))
            {
                continue;
            }

            var deployment = deployments.FirstOrDefault(item => item.Name() == dependencyName);
            var statefulSet = statefulSets.FirstOrDefault(item => item.Name() == dependencyName);
            if (deployment == null && statefulSet == null)
            {
                return false;
            }

            var deploymentReady = (deployment?.Status?.AvailableReplicas ?? 0) > 0;
            var statefulSetReady = (statefulSet?.Status?.ReadyReplicas ?? 0) > 0;
            var isReady = condition switch
            {
                ServiceCondition.ServiceStarted => deploymentReady || statefulSetReady,
                ServiceCondition.ServiceHealthy =>
                    (deployment?.Status?.Conditions?.Any(item => item.Type == "Available" && item.Status == "True") ?? false) ||
                    (statefulSet?.Status?.Conditions?.Any(item => item.Type == "Available" && item.Status == "True") ?? false),
                ServiceCondition.ServiceCompletedSuccessfully =>
                    ((deployment?.Status?.Replicas ?? 0) == 0 && deployment?.Metadata?.DeletionTimestamp != null) ||
                    ((statefulSet?.Status?.Replicas ?? 0) == 0 && statefulSet?.Metadata?.DeletionTimestamp != null),
                _ => false,
            };

            if (!isReady)
            {
                return false;
            }
        }

        return true;
    }
}
