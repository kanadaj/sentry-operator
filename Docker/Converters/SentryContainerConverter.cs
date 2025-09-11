using k8s.Models;
using SentryOperator.Docker.Volume;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public class SentryContainerConverter : ContainerConverter
{
    public override bool CanConvert(string name, DockerService service)
    {
        return service.Image.Contains("getsentry/sentry") || service.Image.Contains("sentry-self-hosted-local");
    }

    protected override V1Container GetBaseContainer(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        var image = sentryDeployment.Spec.Config?.Image ??
                    $"{sentryDeployment.Spec.Config?.Registry + (sentryDeployment.Spec.Config?.Registry == null || sentryDeployment.Spec.Config.Registry.EndsWith('/') ? "" : "/")}getsentry/sentry:{sentryDeployment.Spec.GetVersion()}";
        service.Image = image;
        var container = base.GetBaseContainer(name, service, sentryDeployment);
        
        if (sentryDeployment.Spec.Environment != null)
        {
            container.Env ??= new List<V1EnvVar>();
            foreach (var envVar in sentryDeployment.Spec.Environment)
            {
                var containerEnvVar = container.Env.FirstOrDefault(x => x.Name == envVar.Key) ?? new V1EnvVar
                {
                    Name = envVar.Key
                };
                containerEnvVar.Value = envVar.Value;
            }
        }
        
        container.EnvFrom ??= new List<V1EnvFromSource>();
        container.EnvFrom.Add(new V1EnvFromSource
        {
            SecretRef = new V1SecretEnvSource("sentry-env")
        });
        
        
        var commandArray =
            ((service.Command as IEnumerable<string>)?.ToArray() ?? (service.Command as string)?.Split(" ")) ??
            Array.Empty<string>();
        var commandString = string.Join(" ", commandArray);
        container.Command = new List<string>
        {
            "/bin/bash",
            "-c"
        };
        
        container.Args = new List<string>
        {
            $"pip install -r /etc/sentry/requirements.txt && exec /docker-entrypoint.sh {commandString}"
        };

        return container;
    }

    protected override IEnumerable<VolumeRef> GetVolumeData(DockerService service, SentryDeployment sentryDeployment)
    {
        var vols = base.GetVolumeData(service, sentryDeployment).ToList();
        foreach(var volume in vols)
        {
            yield return volume;
        }

        if (vols.All(x => x.Name != "sentry-data"))
        {
            yield return new VolumeRef("sentry-data", "/data");
        }
    }

    protected override IEnumerable<V1Volume> GetVolumes(DockerService service, SentryDeployment sentryDeployment)
    {
        foreach(var volume in base.GetVolumes(service, sentryDeployment))
        {
            yield return volume;
        }

        yield return new V1Volume("sentry-config", secret: new V1SecretVolumeSource(420, optional: false, secretName: "sentry-config"));
    }

    protected override IEnumerable<V1VolumeMount> GetVolumeMounts(DockerService service, SentryDeployment sentryDeployment)
    {
        foreach(var volumeMount in base.GetVolumeMounts(service, sentryDeployment))
        {
            yield return volumeMount;
        }

        yield return new V1VolumeMount
        {
            Name = "sentry-config",
            MountPath = "/etc/sentry/sentry.conf.py",
            SubPath = "sentry.conf.py"
        };
        
        yield return new V1VolumeMount
        {
            Name = "sentry-config",
            MountPath = "/etc/sentry/requirements.txt",
            SubPath = "requirements.txt"
        };
        
        yield return new V1VolumeMount
        {
            Name = "sentry-config",
            MountPath = "/etc/sentry/config.yml",
            SubPath = "config.yml"
        };
    }
}