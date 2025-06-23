using k8s.Models;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public class TaskBrokerConverter : ContainerConverter
{
    public override bool CanConvert(string name, DockerService service) => name == "taskbroker";

    protected override V1Container GetBaseContainer(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        var container = base.GetBaseContainer(name, service, sentryDeployment);
        container.Ports ??= new List<V1ContainerPort>();
        container.Ports.Add(new V1ContainerPort
        {
            ContainerPort = 50051,
            Name = "taskbroker"
        });
        return container;
    }
}