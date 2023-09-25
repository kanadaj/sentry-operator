using k8s;
using k8s.Models;
using SentryOperator.Docker.Volume;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public abstract class ContainerConverter : IDockerContainerConverter
{
    public virtual int Priority => 0;
    public abstract bool CanConvert(string name, DockerService service);

    public bool TryConvert(string name, DockerService service, SentryDeployment sentryDeployment,
        out IKubernetesObject<V1ObjectMeta>[]? resource)
    {
        if (CanConvert(name, service))
        {
            resource = Convert(name, service, sentryDeployment).ToArray();
            return true;
        }

        resource = null;
        return false;
    }

    public virtual IEnumerable<IKubernetesObject<V1ObjectMeta>> Convert(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        var deployment = CreateDeployment(name, service, sentryDeployment);

        yield return deployment;

        var svc = GetService(name, service, sentryDeployment);
        if (svc != null)
        {
            yield return svc;
        }
    }

    protected virtual V1Container GetBaseContainer(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        var commandArray =
            ((service.Command as IEnumerable<string>)?.ToArray() ?? (service.Command as string)?.Split(" ")) ??
            Array.Empty<string>();
        var commandString = string.Join(" ", commandArray);
        var testCommands = service.Healthcheck?.Test is string testString
            ? new[] { testString }
            : (service.Healthcheck?.Test as IEnumerable<object>)?.Select(x => x.ToString())
            .Where(x => x != "CMD" && x != "CMD-SHELL").ToArray();
        var container = new V1Container()
        {
            Name = name,
            Image = service.Image,
            Command = null,
            Args = service.Command == null ? null : commandArray,
            Env = service.Environment?.Select(x => new V1EnvVar
            {
                Name = x.Key,
                Value = x.Value
            }).ToList(),
            Ports = service.Ports?.Select(x => new V1ContainerPort
            {
                ContainerPort = int.Parse(x.Split(":")[0])
            }).ToList(),
            VolumeMounts = GetVolumeMounts(service, sentryDeployment).ToList(),
            Resources = new V1ResourceRequirements()
            {
                Requests = GetRequests(name, sentryDeployment),
                Limits = GetLimits(name, sentryDeployment)
            },
            LivenessProbe = service.Healthcheck != null
                ? new V1Probe
                {
                    Exec = new V1ExecAction
                    {
                        Command = testCommands
                    },
                    InitialDelaySeconds = int.Parse(service.Healthcheck.StartPeriod?.Trim('s') ?? "0"),
                    PeriodSeconds = int.Parse(service.Healthcheck.Interval?.Trim('s') ?? "0"),
                    TimeoutSeconds = int.Parse(service.Healthcheck.Timeout?.Trim('s') ?? "0"),
                    FailureThreshold = int.Parse(service.Healthcheck.Retries?.ToString() ?? "0"),
                }
                : null,
            ReadinessProbe = service.Healthcheck != null
                ? new V1Probe
                {
                    Exec = new V1ExecAction
                    {
                        Command = testCommands
                    },
                    InitialDelaySeconds = int.Parse(service.Healthcheck.StartPeriod?.Trim('s') ?? "0"),
                    PeriodSeconds = int.Parse(service.Healthcheck.Interval?.Trim('s') ?? "0"),
                    TimeoutSeconds = int.Parse(service.Healthcheck.Timeout?.Trim('s') ?? "0"),
                    FailureThreshold = int.Parse(service.Healthcheck.Retries?.ToString() ?? "0"),
                }
                : null,
        };

        return container;
    }

    protected virtual IEnumerable<V1VolumeMount> GetVolumeMounts(DockerService service, SentryDeployment sentryDeployment)
    {
        return GetVolumeData(service, sentryDeployment).Select(x => new V1VolumeMount
        {
            Name = x.Name,
            MountPath = x.Path,
            SubPath = x.SubPath
        });
    }

    protected virtual IEnumerable<V1Volume> GetVolumes(DockerService service, SentryDeployment sentryDeployment)
    {
        return GetVolumeData(service, sentryDeployment).Select(x => x is SecretVolumeRef secretRef 
            ? new V1Volume(x.Name, secret: new V1SecretVolumeSource(420, secretName: secretRef.SecretName))
            : x is ConfigMapVolumeRef configMapRef
            ? new V1Volume(x.Name, configMap: new V1ConfigMapVolumeSource(name: configMapRef.ConfigMapName ?? x.Name))
            : new V1Volume(x.Name, persistentVolumeClaim: new V1PersistentVolumeClaimVolumeSource(x.Name)));
    }

    protected virtual IEnumerable<VolumeRef> GetVolumeData(DockerService service, SentryDeployment sentryDeployment)
    {
        foreach(var volumeObject in service.Volumes?.AsEnumerable() ?? Array.Empty<object>())
        {
            var volume = volumeObject is string s ? s : null;
            if (volume == null) continue;
            var volumeParts = volume.Split(':');
            var volumeName = volumeParts[0].Trim('.', '/');
            var volumePath = volumeParts[1];

            // Skip the config volume, we mount ConfigMaps and Secrets instead
            if (volumeName == "sentry")
            {
                continue;
            }

            V1VolumeMount volumeMount;
            if (volumeName == "certificates")
            {
                // We mount certificates generated by cert-manager
                volumeMount = new V1VolumeMount
                {
                    Name = volumeName,
                    MountPath = volumePath
                };

                yield return new SecretVolumeRef(volumeName, volumePath, sentryDeployment.Spec.Certificate?.SecretName ?? (sentryDeployment.Name() + "-certificate"));
                continue;
            }
            
            volumeMount = new V1VolumeMount
            {
                Name = volumeName,
                MountPath = volumePath
            };

            yield return new VolumeRef(volumeName, volumePath);
        }
    }

    protected V1Deployment CreateDeployment(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        return new V1Deployment
        {
            ApiVersion = "apps/v1",
            Kind = "Deployment",
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = sentryDeployment.Namespace(),
                Labels = new Dictionary<string, string>
                {
                    { "app.kubernetes.io/name", name },
                    { "app.kubernetes.io/instance", name },
                    { "app.kubernetes.io/version", sentryDeployment.Spec.GetVersion() },
                    { "app.kubernetes.io/managed-by", "sentry-operator" },
                }
            },
            Spec = new V1DeploymentSpec
            {
                Strategy = new V1DeploymentStrategy
                {
                    Type = "RollingUpdate",
                    RollingUpdate = new V1RollingUpdateDeployment
                    {
                        MaxSurge = "25%",
                        MaxUnavailable = "25%"
                    }
                },
                Selector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>
                    {
                        { "app.kubernetes.io/name", name },
                        { "app.kubernetes.io/instance", name },
                    }
                },
                Replicas =
                    (sentryDeployment.Spec.Replicas?.TryGetValue(name, out var replicas) ?? false) ? replicas : 1,
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = new Dictionary<string, string>
                        {
                            { "app.kubernetes.io/name", name },
                            { "app.kubernetes.io/instance", name },
                        }
                    },
                    Spec = GeneratePodSpec(name, service, sentryDeployment)
                }
            }
        };
    }

    protected virtual V1PodSpec GeneratePodSpec(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        var baseContainer = GetBaseContainer(name, service, sentryDeployment);
        
        var podSpec = new V1PodSpec
        {
            Containers = new List<V1Container>
            {
                baseContainer
            },
            Volumes = GetVolumes(service, sentryDeployment).ToList()
        };

        return podSpec;
    }

    public virtual V1Service? GetService(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        if(service.Ports == null || service.Ports.Count == 0)
        {
            return null;
        }
        
        
        var svc = new V1Service()
        {
            Metadata = new V1ObjectMeta
            {
                // We can't name our service "relay" because Relay itself will react to the service env var
                Name = name == "relay" ? "relay-service" : name,
                NamespaceProperty = sentryDeployment.Namespace(),
                Labels = new Dictionary<string, string>
                {
                    { "app.kubernetes.io/name", name },
                    { "app.kubernetes.io/instance", name },
                    { "app.kubernetes.io/version", sentryDeployment.Spec.GetVersion() },
                    { "app.kubernetes.io/managed-by", "sentry-operator" },
                }
            },
            Spec = new V1ServiceSpec
            {
                Ports = service.Ports?.Select(x => new V1ServicePort()
                {
                    Port = int.Parse(x.Split(":")[0]),
                    TargetPort = int.Parse(x.Split(":")[0]),
                    Name = $"{name}-{x.Split(":")[0]}",
                }).ToList(),
                Selector = new Dictionary<string, string>
                {
                    {"app.kubernetes.io/name", name }
                }
            }
        };

        return svc;
    }

    protected virtual IDictionary<string, ResourceQuantity>? GetRequests(string name, SentryDeployment sentryDeployment)
    {
        return name switch
        {
            "web" => sentryDeployment.Spec.Resources?.Web?.Requests ?? null,
            "worker" => sentryDeployment.Spec.Resources?.Worker?.Requests ?? new Dictionary<string, ResourceQuantity>
                { { "cpu", new ResourceQuantity("100m") }, { "memory", new ResourceQuantity("2Gi") }, },
            "cron" => sentryDeployment.Spec.Resources?.Cron?.Requests ?? new Dictionary<string, ResourceQuantity>
                { { "cpu", new ResourceQuantity("25m") }, { "memory", new ResourceQuantity("250Mi") }, },
            "snuba-api" => sentryDeployment.Spec.Resources?.Snuba?.Requests ?? new Dictionary<string, ResourceQuantity>
                { { "cpu", new ResourceQuantity("25m") }, { "memory", new ResourceQuantity("110Mi") }, },
            "relay" => sentryDeployment.Spec.Resources?.Relay?.Requests ?? new Dictionary<string, ResourceQuantity>
                { { "cpu", new ResourceQuantity("20m") }, { "memory", new ResourceQuantity("500Mi") }, },
            _ when name.Contains("consumer") => sentryDeployment.Spec.Resources?.Consumer?.Requests ??
                                                new Dictionary<string, ResourceQuantity>
                                                {
                                                    { "cpu", new ResourceQuantity("10m") },
                                                    { "memory", new ResourceQuantity("200Mi") },
                                                },
            _ when name.Contains("ingest") => sentryDeployment.Spec.Resources?.Ingest?.Requests ??
                                              new Dictionary<string, ResourceQuantity>
                                              {
                                                  { "cpu", new ResourceQuantity("10m") },
                                                  { "memory", new ResourceQuantity("200Mi") },
                                              },
            _ when name.Contains("forwarder") => sentryDeployment.Spec.Resources?.Forwarder?.Requests ??
                                                 new Dictionary<string, ResourceQuantity>
                                                 {
                                                     { "cpu", new ResourceQuantity("10m") },
                                                     { "memory", new ResourceQuantity("200Mi") },
                                                 },
            _ when name.Contains("replacer") => sentryDeployment.Spec.Resources?.Replacer?.Requests ??
                                                new Dictionary<string, ResourceQuantity>
                                                {
                                                    { "cpu", new ResourceQuantity("10m") },
                                                    { "memory", new ResourceQuantity("200Mi") },
                                                },
            _ => null
        };
    }

    protected virtual IDictionary<string, ResourceQuantity>? GetLimits(string name, SentryDeployment sentryDeployment)
    {
        return name switch
        {
            "web" => sentryDeployment.Spec.Resources?.Web?.Limits ?? null,
            "worker" => sentryDeployment.Spec.Resources?.Worker?.Limits ?? new Dictionary<string, ResourceQuantity>
                { { "cpu", new ResourceQuantity("200m") }, { "memory", new ResourceQuantity("2.5Gi") }, },
            "cron" => sentryDeployment.Spec.Resources?.Cron?.Limits ?? new Dictionary<string, ResourceQuantity>
                { { "cpu", new ResourceQuantity("50m") }, { "memory", new ResourceQuantity("1Gi") }, },
            "snuba-api" => sentryDeployment.Spec.Resources?.Snuba?.Limits ?? new Dictionary<string, ResourceQuantity>
                { { "cpu", new ResourceQuantity("50m") }, { "memory", new ResourceQuantity("200Mi") }, },
            "relay" => sentryDeployment.Spec.Resources?.Relay?.Limits ?? new Dictionary<string, ResourceQuantity>
                { { "cpu", new ResourceQuantity("100m") }, { "memory", new ResourceQuantity("1Gi") }, },
            _ when name.Contains("consumer") => sentryDeployment.Spec.Resources?.Consumer?.Limits ??
                                                new Dictionary<string, ResourceQuantity>
                                                {
                                                    { "cpu", new ResourceQuantity("50m") },
                                                    { "memory", new ResourceQuantity("500Mi") },
                                                },
            _ when name.Contains("ingest") => sentryDeployment.Spec.Resources?.Ingest?.Limits ??
                                              new Dictionary<string, ResourceQuantity>
                                              {
                                                  { "cpu", new ResourceQuantity("50m") },
                                                  { "memory", new ResourceQuantity("500Mi") },
                                              },
            _ when name.Contains("forwarder") => sentryDeployment.Spec.Resources?.Forwarder?.Limits ??
                                                 new Dictionary<string, ResourceQuantity>
                                                 {
                                                     { "cpu", new ResourceQuantity("100m") },
                                                     { "memory", new ResourceQuantity("500Mi") },
                                                 },
            _ when name.Contains("replacer") => sentryDeployment.Spec.Resources?.Replacer?.Limits ??
                                                new Dictionary<string, ResourceQuantity>
                                                {
                                                    { "cpu", new ResourceQuantity("100m") },
                                                    { "memory", new ResourceQuantity("500Mi") },
                                                },
            _ when name.Contains("geoip") => sentryDeployment.Spec.Resources?.GeoIP?.Limits ??
                                             new Dictionary<string, ResourceQuantity>
                                             {
                                                 { "cpu", new ResourceQuantity("100m") },
                                                 { "memory", new ResourceQuantity("500Mi") },
                                             },
            _ => null
        };
    }
}