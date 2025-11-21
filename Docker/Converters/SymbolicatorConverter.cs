using k8s.Models;
using SentryOperator.Docker.Volume;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public class SymbolicatorConverter : ContainerConverter
{
    public override bool CanConvert(string name, DockerService service) => name == "symbolicator";

    protected override V1Container GetBaseContainer(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        var container = base.GetBaseContainer(name, service, sentryDeployment);

        container.Ports ??= new List<V1ContainerPort>();
        container.Ports.Add(new V1ContainerPort(3021)
        {
            Name = "http",
            Protocol = "TCP"
        });

        container.SecurityContext.RunAsUser = 1001;
        
        return container;
    }

    protected override IEnumerable<VolumeRef> GetVolumeData(DockerService service, SentryDeployment sentryDeployment)
    {
        foreach(var volume in base.GetVolumeData(service, sentryDeployment))
        {
            yield return volume;
        }
        
        yield return new ConfigMapVolumeRef("symbolicator-conf", "/etc/symbolicator/config.yml", "config.yml");
    }
}