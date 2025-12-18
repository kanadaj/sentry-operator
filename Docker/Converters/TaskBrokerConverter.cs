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

    protected override IEnumerable<V1Volume> GetVolumes(DockerService service, SentryDeployment sentryDeployment)
    {
        var vols = base.GetVolumes(service, sentryDeployment);
        foreach(var volume in vols)
        {
            if (volume.Name == "sentry-taskbroker")
            {
                yield return new V1Volume("sentry-taskbroker", emptyDir: new V1EmptyDirVolumeSource());
            }
            else
            {
                yield return volume;
            }
        }
    }
}