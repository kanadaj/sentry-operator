using k8s.Models;
using SentryOperator.Docker.Compose;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public class MemcachedConverter : ContainerConverter
{
    public override bool CanConvert(string name, DockerService service) => name == "memcached";

    protected override V1Container GetBaseContainer(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        var container = base.GetBaseContainer(name, service, sentryDeployment);

        container.LivenessProbe = null;
        container.ReadinessProbe = null;

        return container;
    }
}