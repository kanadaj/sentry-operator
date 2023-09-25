using k8s;
using k8s.Models;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public interface IDockerContainerConverter
{
    int Priority { get; }
    bool CanConvert(string name, DockerService service);

    IEnumerable<IKubernetesObject<V1ObjectMeta>> Convert(string name, DockerService service, SentryDeployment sentryDeployment);
    
    bool TryConvert(string name, DockerService service, SentryDeployment sentryDeployment, out IKubernetesObject<V1ObjectMeta>[]? resource);
}