using k8s.Models;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public class VroomConverter : DefaultConverter
{
    protected override V1Container GetBaseContainer(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        var container = base.GetBaseContainer(name, service, sentryDeployment);
        container.Ports ??= new List<V1ContainerPort>();
        container.Ports.Add(new V1ContainerPort()
        {
            ContainerPort = 8085
        });
        return container;
    }
}