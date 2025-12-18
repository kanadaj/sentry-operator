using k8s.Models;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public class SnubaConsumerConverter : ContainerConverter
{
    public override bool CanConvert(string name, DockerService service) => service.Command?.ToString()?.StartsWith("rust-consumer") ?? false;

    protected override V1PodSpec GeneratePodSpec(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        var podSpec = base.GeneratePodSpec(name, service, sentryDeployment);
        
        foreach (var container in podSpec.Containers)
        {
            container.EnvFrom ??= new List<V1EnvFromSource>();
            container.EnvFrom.Add(new V1EnvFromSource(new V1ConfigMapEnvSource("snuba-env")));
        }

        var firstContainer = podSpec.Containers.First();

        firstContainer.Args ??= new List<string>();
        firstContainer.Args.Add("--concurrency");
        firstContainer.Args.Add("4");

        if ((sentryDeployment.Spec.Replicas?.TryGetValue("taskbroker", out var brokerCount) ?? false) && brokerCount > 1)
        {
            firstContainer.Args.Add("--num-brokers="+ brokerCount);   
        }
        
        return podSpec;
    }
}