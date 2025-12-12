using k8s.Models;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public class SnubaConverter : ContainerConverter
{
    public override bool CanConvert(string name, DockerService service) => service.Image?.Contains("getsentry/snuba") ?? false;

    protected override V1PodSpec GeneratePodSpec(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        var pod = base.GeneratePodSpec(name, service, sentryDeployment);
        
        foreach (var container in pod.Containers)
        {
            container.EnvFrom ??= new List<V1EnvFromSource>();
            container.EnvFrom.Add(new V1EnvFromSource
            {
                SecretRef = new V1SecretEnvSource("snuba-env")
            });
        }
        
        return pod;
    }
}