using k8s.Models;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public abstract class CleanupConverter : ContainerConverter
{
    protected abstract string GetImage(string version);
    protected abstract string CronSchedule { get; }
    protected abstract string CronTask { get; }
    
    protected override V1Container GetBaseContainer(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        service.Image = GetImage(sentryDeployment.Spec.GetVersion());
        
        var container = base.GetBaseContainer(name, service, sentryDeployment);
        
        container.SecurityContext ??= new V1SecurityContext();
        container.SecurityContext.RunAsUser = 0;
        
        container.Command = new List<string>
        {
            "/bin/bash",
            "-c"
        };
        
        container.Args = new List<string>
        {
            $"apt-get update && apt-get install -y --no-install-recommends cron && rm -r /var/lib/apt/lists/* && exec /entrypoint.sh \"{CronSchedule} {CronTask}\""
        };

        return container;
    }

    protected override IEnumerable<V1Volume> GetVolumes(DockerService service, SentryDeployment sentryDeployment)
    {
        foreach(var volume in base.GetVolumes(service, sentryDeployment))
        {
            yield return volume;
        }

        yield return new V1Volume
        {
            Name = "sentry-cron",
            ConfigMap = new V1ConfigMapVolumeSource
            {
                Name = "sentry-cron",
                Optional = false,
                DefaultMode = 344
            }
        };
    }

    protected override IEnumerable<V1VolumeMount> GetVolumeMounts(DockerService service, SentryDeployment sentryDeployment)
    {
        foreach(var volumeMount in base.GetVolumeMounts(service, sentryDeployment))
        {
            yield return volumeMount;
        }
        
        yield return new V1VolumeMount
        {
            Name = "sentry-cron",
            MountPath = "/entrypoint.sh",
            SubPath = "entrypoint.sh"
        };
    }
}