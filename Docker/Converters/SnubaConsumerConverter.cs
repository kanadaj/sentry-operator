using System.Text;
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
            // We remove the rpc-host option and instead build a rpc-host-list from 0 to N-1 using taskbroker-N.taskbroker:50051
            var arg = firstContainer.Args.FirstOrDefault(x => x.Contains("--rpc-host"));
            if (arg != null)
            {
                firstContainer.Args.Remove(arg);
            }

            var option = new StringBuilder("--rpc-host-list=");
            for (int i = 0; i < brokerCount; i++)
            {
                option.Append("taskbroker-").Append(i).Append(".taskbroker:50051").Append(",");
            }
            option.Remove(option.Length - 1, 1);
            firstContainer.Args.Add(option.ToString());
        }
        
        return podSpec;
    }
}