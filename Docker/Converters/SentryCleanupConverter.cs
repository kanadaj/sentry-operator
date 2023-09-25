using k8s.Models;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public class SentryCleanupConverter : SentryContainerConverter
{
    public override int Priority => 1;

    public override bool CanConvert(string name, DockerService service) => service.Image == "sentry-cleanup-self-hosted-local";

    protected override V1Container GetBaseContainer(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        service.Image = $"getsentry/sentry:{sentryDeployment.Spec.GetVersion()}";
        
        var container = base.GetBaseContainer(name, service, sentryDeployment);
        
        container.Command = new List<string>
        {
            "/bin/bash",
            "-c"
        };
        
        container.Args = new List<string>
        {
            "apt-get update && apt-get install -y --no-install-recommends cron && rm -r /var/lib/apt/lists/* && pip install -r /etc/sentry/requirements.txt && exec /entrypoint.sh \"0 0 * * * gosu sentry sentry cleanup --days $SENTRY_EVENT_RETENTION_DAYS\""
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