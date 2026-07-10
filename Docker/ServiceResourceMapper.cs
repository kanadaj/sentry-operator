using k8s;
using k8s.Models;
using SentryOperator.Extensions;

namespace SentryOperator.Docker;

public class ServiceResourceMapper
{
    public Dictionary<string, List<IKubernetesObject<V1ObjectMeta>>> Map(
        IEnumerable<IKubernetesObject<V1ObjectMeta>> resources)
    {
        var mappedResources = new Dictionary<string, List<IKubernetesObject<V1ObjectMeta>>>();
        foreach (var resource in resources)
        {
            var serviceName = resource.GetLabel("app.kubernetes.io/part-of") ??
                              resource.GetLabel("app.kubernetes.io/name");
            if (string.IsNullOrEmpty(serviceName))
            {
                continue;
            }

            if (!mappedResources.TryGetValue(serviceName, out var serviceResources))
            {
                serviceResources = [];
                mappedResources[serviceName] = serviceResources;
            }

            serviceResources.Add(resource);
        }

        return mappedResources;
    }
}
