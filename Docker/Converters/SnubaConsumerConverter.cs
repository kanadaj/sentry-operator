using k8s.Models;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public class SnubaConsumerConverter : ContainerConverter
{
    public override bool CanConvert(string name, DockerService service) => service.Command?.ToString()?.StartsWith("rust-consumer") ?? false;

    protected override V1PodSpec GeneratePodSpec(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        var podSpec = base.GeneratePodSpec(name, service, sentryDeployment);
        
        var container = podSpec.Containers.First();

        container.Args ??= new List<string>();
        container.Args.Add("--concurrency");
        container.Args.Add("4");
        
        return podSpec;
    }
}