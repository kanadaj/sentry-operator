﻿using k8s;
using k8s.Models;
using SentryOperator.Docker.Converters;
using SentryOperator.Entities;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace SentryOperator.Docker;

public class DockerComposeConverter
{
    private readonly IEnumerable<IDockerContainerConverter> _converters;
    private readonly string[] _ignoredServices = new[]
    {
        "smtp",
        "memcached",
        "redis",
        "postgres",
        "clickhouse",
        "zookeeper",
        "kafka",
        "nginx"
    };

    private readonly ILogger _logger;

    public DockerComposeConverter(ILogger logger, IEnumerable<IDockerContainerConverter> converters)
    {
        _logger = logger;
        _converters = converters;
    }

    public List<IKubernetesObject<V1ObjectMeta>> Convert(string dockerComposeYaml,
        SentryDeployment sentryDeployment)
    {
        var dockerCompose = Parse(dockerComposeYaml);
        var result = new List<IKubernetesObject<V1ObjectMeta>>(); 
        foreach (var service in dockerCompose.Services!)
        {
            if (_ignoredServices.Contains(service.Key))
            {
                _logger.LogInformation("Ignoring service {ServiceName}", service.Key);
                continue;
            }
            _logger.LogInformation("Converting service {ServiceName}", service.Key);
            var converter = _converters.OrderByDescending(x => x.Priority).First(x => x.CanConvert(service.Key, service.Value));

            var resources = converter.Convert(service.Key, service.Value, sentryDeployment).ToList();
            foreach (var resource in resources)
            {
                _logger.LogInformation("Converted {ResourceType} {ResourceName}", resource.Kind, resource.Metadata.Name);
            }

            result.AddRange(resources);
        }

        return result;
    }

    public DockerCompose Parse(string dockerComposeYaml)
    {
        var mergingParser = new MergingParser(new Parser(new StringReader(dockerComposeYaml)));
        var dockerComposeFile = new DeserializerBuilder()
            .Build()
            .Deserialize<DockerCompose>(mergingParser);

        return dockerComposeFile;
    }

    // public (V1Deployment[] Deployments, V1Service[] Services) ConvertOld(string dockerComposeYaml, SentryDeployment sentryDeployment, string version = "nightly")
    // {
    //     var dockerCompose = Parse(dockerComposeYaml);
    //     var deployments = new List<V1Deployment>();
    //     
    //     _logger.LogInformation("Converting docker-compose.yml to Kubernetes manifests");
    //
    //     foreach (var service in dockerCompose.Services!)
    //     {
    //         _logger.LogInformation("Converting service {ServiceName}", service.Key);
    //         if (_ignoredServices.Contains(service.Key)) continue;
    //         
    //         if (service.Value.Image == "sentry-self-hosted-local" || (service.Value.Image?.StartsWith("getsentry/sentry:") ?? true))
    //         {
    //             service.Value.Image = $"getsentry/sentry:{version}";
    //         }
    //         else if (service.Value.Image == "$SNUBA_IMAGE" || (service.Value.Image?.StartsWith("getsentry/snuba:") ?? true))
    //         {
    //             service.Value.Image = $"getsentry/snuba:{version}";
    //         }
    //         else if (service.Value.Image.Contains("nightly") && !service.Value.Image.Contains("symbolicator")) // Symbolicator is versioned differently
    //         {
    //             service.Value.Image = service.Value.Image.Replace("nightly", version);
    //         }
    //
    //         var deployment = new V1Deployment
    //         {
    //             ApiVersion = "apps/v1",
    //             Kind = "Deployment",
    //             Metadata = new V1ObjectMeta
    //             {
    //                 Name = service.Key,
    //                 NamespaceProperty = sentryDeployment.Namespace(),
    //                 Labels = new Dictionary<string, string>
    //                 {
    //                     { "app.kubernetes.io/name", service.Key },
    //                     { "app.kubernetes.io/instance", service.Key },
    //                     { "app.kubernetes.io/version", version },
    //                     { "app.kubernetes.io/managed-by", "sentry-operator" },
    //                 }
    //             },
    //             Spec = new V1DeploymentSpec
    //             {
    //                 Strategy = new V1DeploymentStrategy
    //                 {
    //                     Type = "RollingUpdate",
    //                     RollingUpdate = new V1RollingUpdateDeployment
    //                     {
    //                         MaxSurge = "25%",
    //                         MaxUnavailable = "25%"
    //                     }
    //                 },
    //                 Selector = new V1LabelSelector
    //                 {
    //                     MatchLabels = new Dictionary<string, string>
    //                     {
    //                         { "app.kubernetes.io/name", service.Key },
    //                         { "app.kubernetes.io/instance", service.Key },
    //                     }
    //                 },
    //                 Replicas = (sentryDeployment.Spec.Replicas?.TryGetValue(service.Key, out var replicas) ?? false) ? replicas : 1,
    //                 Template = new V1PodTemplateSpec
    //                 {
    //                     Metadata = new V1ObjectMeta
    //                     {
    //                         Labels = new Dictionary<string, string>
    //                         {
    //                             { "app.kubernetes.io/name", service.Key },
    //                             { "app.kubernetes.io/instance", service.Key },
    //                         }
    //                     },
    //                     Spec = GeneratePodSpec(service, sentryDeployment, version)
    //                 }
    //             }
    //         };
    //         
    //         deployments.Add(deployment);
    //     }
    //     
    //     var services = new List<V1Service>();
    //
    //     /*
    //      * Pods that need services:
    //      * web: 9000
    //      * snuba-api: 1218
    //      * symbolicator: 3021
    //      * relay: 3000
    //      */
    //     _logger.LogInformation("Generating K8s services");
    //     foreach (var (service, port) in new Dictionary<string, int>
    //              {
    //                     { "web", 9000 },
    //                     { "snuba-api", 1218 },
    //                     { "symbolicator", 3021 },
    //                     { "relay", 3000 },
    //                     { "vroom", 8085 },
    //              })
    //     {
    //         var svc = new V1Service()
    //         {
    //             Metadata = new V1ObjectMeta
    //             {
    //                 // We can't name our service "relay" because Relay itself will react to the service env var
    //                 Name = service == "relay" ? "relay-service" : service,
    //                 NamespaceProperty = sentryDeployment.Namespace(),
    //                 Labels = new Dictionary<string, string>
    //                 {
    //                     { "app.kubernetes.io/name", service },
    //                     { "app.kubernetes.io/instance", service },
    //                     { "app.kubernetes.io/version", version },
    //                     { "app.kubernetes.io/managed-by", "sentry-operator" },
    //                 }
    //             },
    //             Spec = new V1ServiceSpec
    //             {
    //                 Ports = new List<V1ServicePort>()
    //                 {
    //                     new V1ServicePort
    //                     {
    //                         Name = service,
    //                         Port = port,
    //                         TargetPort = port,
    //                     }
    //                 },
    //                 Selector = new Dictionary<string, string>
    //                 {
    //                     {"app.kubernetes.io/name", service }
    //                 }
    //             }
    //         };
    //         services.Add(svc);
    //     }
    //     
    //     return (deployments.ToArray(), services.ToArray());
    // }
    //
    // private V1PodSpec GeneratePodSpec(KeyValuePair<string, DockerService> service, SentryDeployment sentryDeployment, string version)
    // {
    //     bool isCleanup = false;
    //     if (service.Value.Image == "sentry-cleanup-self-hosted-local")
    //     {
    //         _logger.LogInformation("Converting service {ServiceName} to cleanup", service.Key);
    //         isCleanup = true;
    //         service.Value.Image = $"getsentry/sentry:{version}";
    //     }
    //     if (service.Value.Image == "symbolicator-cleanup-self-hosted-local")
    //     {
    //         _logger.LogInformation("Converting service {ServiceName} to cleanup", service.Key);
    //         //isCleanup = true;
    //         service.Value.Image = $"getsentry/symbolicator:nightly";
    //     }
    //     if (service.Value.Image == "vroom-cleanup-self-hosted-local")
    //     {
    //         _logger.LogInformation("Converting service {ServiceName} to cleanup", service.Key);
    //         //isCleanup = true;
    //         service.Value.Image = $"getsentry/vroom:{version}";
    //     }
    //     
    //
    //     if (service.Value.Image.Contains("geoipupdate"))
    //     {
    //         service.Value.Environment ??= new Dictionary<string, string>();
    //         service.Value.Environment["GEOIPUPDATE_LICENSE_KEY"] = sentryDeployment.Spec.Environment["GEOIPUPDATE_LICENSE_KEY"];
    //         service.Value.Environment["GEOIPUPDATE_ACCOUNT_ID"] = sentryDeployment.Spec.Environment["GEOIPUPDATE_ACCOUNT_ID"];
    //         service.Value.Environment["GEOIPUPDATE_EDITION_IDS"] = sentryDeployment.Spec.Environment["GEOIPUPDATE_EDITION_IDS"];
    //     }
    //     
    //     _logger.LogInformation("Generating pod spec for {ServiceName}", service.Key);
    //     var container = service.Value.Image!.Contains("/sentry", StringComparison.OrdinalIgnoreCase) ? GenerateSentryContainer(service.Key, sentryDeployment, service.Value) : GenerateSnubaContainer(service.Key, sentryDeployment, service.Value);
    //     var podSpec = new V1PodSpec
    //     {
    //         Containers = new List<V1Container>
    //         {
    //             container
    //         },
    //         Volumes = new List<V1Volume>()
    //         {
    //             
    //         }
    //     };
    //     
    //     if (isCleanup)
    //     {
    //         _logger.LogInformation("Adding cleanup specific configuration for {ServiceName}", service.Key);
    //         container.VolumeMounts.Add(new V1VolumeMount
    //         {
    //             Name = "sentry-cron",
    //             MountPath = "/entrypoint.sh",
    //             SubPath = "entrypoint.sh"
    //         });
    //         podSpec.Volumes.Add(new V1Volume
    //         {
    //             Name = "sentry-cron",
    //             ConfigMap = new V1ConfigMapVolumeSource
    //             {
    //                 Name = "sentry-cron",
    //                 Optional = false,
    //                 DefaultMode = 344
    //             }
    //         });
    //         container.Command = new List<string>
    //         {
    //             "/bin/bash",
    //             "-c"
    //         };
    //         container.Args = new List<string>
    //         {
    //             "apt-get update && apt-get install -y --no-install-recommends cron && rm -r /var/lib/apt/lists/* && pip install -r /etc/sentry/requirements.txt && exec /entrypoint.sh \"0 0 * * * gosu sentry sentry cleanup --days $SENTRY_EVENT_RETENTION_DAYS\""
    //         };
    //     }
    //
    //     if (service.Key == "symbolicator")
    //     {
    //         podSpec.Volumes.Add(new V1Volume("symbolicator-conf", configMap: new V1ConfigMapVolumeSource(name: "symbolicator-conf")));
    //         container.VolumeMounts.Add(new V1VolumeMount
    //         {
    //             Name = "symbolicator-conf",
    //             MountPath = "/etc/symbolicator/config.yml",
    //             SubPath = "config.yml"
    //         });
    //     }
    //
    //     if (service.Key == "symbolicator-cleanup")
    //     {
    //         podSpec.Volumes.Add(new V1Volume("symbolicator-conf", configMap: new V1ConfigMapVolumeSource(name: "symbolicator-conf")));
    //         container.VolumeMounts.Add(new V1VolumeMount
    //         {
    //             Name = "symbolicator-conf",
    //             MountPath = "/etc/symbolicator/config.yml",
    //             SubPath = "config.yml"
    //         });
    //         container.Command = new List<string>
    //         {
    //             "/bin/bash",
    //             "-c"
    //         };
    //         container.VolumeMounts.Add(new V1VolumeMount
    //         {
    //             Name = "sentry-cron",
    //             MountPath = "/entrypoint.sh",
    //             SubPath = "entrypoint.sh"
    //         });
    //         podSpec.Volumes.Add(new V1Volume
    //         {
    //             Name = "sentry-cron",
    //             ConfigMap = new V1ConfigMapVolumeSource
    //             {
    //                 Name = "sentry-cron",
    //                 Optional = false,
    //                 DefaultMode = 344
    //             }
    //         });
    //         container.Args = new List<string>
    //         {
    //             "apt-get update && apt-get install -y --no-install-recommends cron && rm -r /var/lib/apt/lists/* && exec /entrypoint.sh \"55 23 * * * gosu symbolicator symbolicator cleanup\""
    //         };
    //     }
    //
    //     if (service.Key == "vroom-cleanup")
    //     {
    //         container.Command = new List<string>
    //         {
    //             "/bin/bash",
    //             "-c"
    //         };
    //         container.VolumeMounts.Add(new V1VolumeMount
    //         {
    //             Name = "sentry-cron",
    //             MountPath = "/entrypoint.sh",
    //             SubPath = "entrypoint.sh"
    //         });
    //         podSpec.Volumes.Add(new V1Volume
    //         {
    //             Name = "sentry-cron",
    //             ConfigMap = new V1ConfigMapVolumeSource
    //             {
    //                 Name = "sentry-cron",
    //                 Optional = false,
    //                 DefaultMode = 344
    //             }
    //         });
    //         container.Args = new List<string>
    //         {
    //             "apt-get update && apt-get install -y --no-install-recommends cron && rm -r /var/lib/apt/lists/* && exec /entrypoint.sh \"0 0 * * * find /var/lib/sentry-profiles -type f -mtime +$SENTRY_EVENT_RETENTION_DAYS -delete\""
    //         };
    //     }
    //
    //     _logger.LogInformation("Adding sentry-env");
    //     container.EnvFrom ??= new List<V1EnvFromSource>();
    //     container.EnvFrom.Add(new V1EnvFromSource
    //     {
    //         SecretRef = new V1SecretEnvSource("sentry-env")
    //     });
    //
    //     if (sentryDeployment.Spec.Environment != null)
    //     {
    //         _logger.LogInformation("Adding environment variables");
    //         container.Env ??= new List<V1EnvVar>();
    //         foreach (var envVar in sentryDeployment.Spec.Environment)
    //         {
    //             var containerEnvVar = container.Env.FirstOrDefault(x => x.Name == envVar.Key) ?? new V1EnvVar
    //             {
    //                 Name = envVar.Key
    //             };
    //             containerEnvVar.Value = envVar.Value;
    //         }
    //     }
    //     
    //     foreach(var volumeObject in service.Value.Volumes?.AsEnumerable() ?? Array.Empty<object>())
    //     {
    //         _logger.LogInformation("Adding volume {Volume}", volumeObject);
    //         var volume = volumeObject is string s ? s : null;
    //         if (volume == null) continue;
    //         var volumeParts = volume.Split(':');
    //         var volumeName = volumeParts[0].Trim('.', '/');
    //         var volumePath = volumeParts[1];
    //
    //         // Skip the config volume, we mount ConfigMaps and Secrets instead
    //         if (volumeName == "sentry")
    //         {
    //             continue;
    //         }
    //
    //         V1VolumeMount volumeMount;
    //         if (volumeName == "certificates")
    //         {
    //             // We mount certificates generated by cert-manager
    //             volumeMount = new V1VolumeMount
    //             {
    //                 Name = volumeName,
    //                 MountPath = volumePath
    //             };
    //             container.VolumeMounts.Add(volumeMount);
    //             podSpec.Volumes.Add(new V1Volume(volumeName, secret: new V1SecretVolumeSource(420, secretName: sentryDeployment.Spec.Certificate?.SecretName ?? (sentryDeployment.Name() + "-certificate"))));
    //             continue;
    //         }
    //         
    //         volumeMount = new V1VolumeMount
    //         {
    //             Name = volumeName,
    //             MountPath = volumePath
    //         };
    //         container.VolumeMounts.Add(volumeMount);
    //         podSpec.Volumes.Add(new V1Volume(volumeName, persistentVolumeClaim: new V1PersistentVolumeClaimVolumeSource(volumeName)));
    //     }
    //
    //     if (service.Value.Image.Contains("relay", StringComparison.OrdinalIgnoreCase))
    //     {
    //         _logger.LogInformation("Adding relay specific configuration");
    //         podSpec.Volumes.Add(new V1Volume("geoip", persistentVolumeClaim: new V1PersistentVolumeClaimVolumeSource("geoip")));
    //         container.VolumeMounts.Add(new V1VolumeMount
    //         {
    //             Name = "geoip",
    //             MountPath = "/geoip"
    //         });
    //         container.Env ??= new List<V1EnvVar>();
    //         container.Env.Add(new V1EnvVar
    //         {
    //             Name = "PORT",
    //             Value = "3000"
    //         });
    //         container.Ports ??= new List<V1ContainerPort>();
    //         container.Ports.Add(new V1ContainerPort
    //         {
    //             Name = "http",
    //             ContainerPort = 3000
    //         });
    //         container.VolumeMounts.Add(new V1VolumeMount
    //         {
    //             Name = "relay-conf",
    //             MountPath = "/work/.relay"
    //         });
    //         podSpec.Volumes.Add(new V1Volume("relay-conf", configMap: new V1ConfigMapVolumeSource(name: "relay-conf")));
    //     }
    //     
    //
    //     if (service.Value.Image!.Contains("sentry", StringComparison.OrdinalIgnoreCase))
    //     {
    //         _logger.LogInformation("Adding sentry specific configuration");
    //         podSpec.Volumes.Add(new V1Volume("sentry-config", secret:  new V1SecretVolumeSource(420, optional: false, secretName: "sentry-config")));
    //
    //         // We don't mount to /data if it's already taken
    //         if (container.VolumeMounts.All(x => x.MountPath != "/data"))
    //         {
    //             container.VolumeMounts.Add(new V1VolumeMount
    //             {
    //                 Name = "sentry-data",
    //                 MountPath = "/data"
    //             });
    //             podSpec.Volumes.Add(new V1Volume("sentry-data", persistentVolumeClaim:new V1PersistentVolumeClaimVolumeSource("sentry-data")));
    //         }
    //         
    //         container.VolumeMounts.Add(new V1VolumeMount
    //         {
    //             Name = "sentry-config",
    //             MountPath = "/etc/sentry/sentry.conf.py",
    //             SubPath = "sentry.conf.py"
    //         });
    //         container.VolumeMounts.Add(new V1VolumeMount
    //         {
    //             Name = "sentry-config",
    //             MountPath = "/etc/sentry/requirements.txt",
    //             SubPath = "requirements.txt"
    //         });
    //         container.VolumeMounts.Add(new V1VolumeMount
    //         {
    //             Name = "sentry-config",
    //             MountPath = "/etc/sentry/config.yml",
    //             SubPath = "config.yml"
    //         });
    //     }
    //
    //     if (service.Key == "snuba-api")
    //     {
    //         var bootstrapContainer = GenerateSnubaContainer("snuba-bootstrap", sentryDeployment, new DockerService()
    //         {
    //             Image = container.Image,
    //         });
    //         
    //         bootstrapContainer.Args = new List<string>
    //         {
    //             "bootstrap",
    //             "--force",
    //             "--no-migrate"
    //         };
    //         
    //         var migrationContainer = GenerateSnubaContainer("snuba-migration", sentryDeployment, new DockerService()
    //         {
    //             Image = container.Image,
    //         });
    //         
    //         migrationContainer.Args = new List<string>
    //         {
    //             "migrations",
    //             "migrate",
    //             "--force"
    //         };
    //         
    //         podSpec.InitContainers = new List<V1Container>
    //         {
    //             bootstrapContainer,
    //             migrationContainer
    //         };
    //     }
    //     
    //     if (service.Key == "web")
    //     {
    //         _logger.LogInformation("Adding web specific configuration");
    //         var initContainer = GenerateSentryContainer("db-setup", sentryDeployment, new DockerService()
    //         {
    //             Image = container.Image,
    //             Command = new[]{"upgrade --noinput"},
    //         });
    //         initContainer.EnvFrom = new List<V1EnvFromSource>
    //         {
    //             new V1EnvFromSource
    //             {
    //                 SecretRef = new V1SecretEnvSource("sentry-env")
    //             }
    //         };
    //         initContainer.VolumeMounts.Add(new V1VolumeMount
    //         {
    //             Name = "sentry-config",
    //             MountPath = "/etc/sentry/sentry.conf.py",
    //             SubPath = "sentry.conf.py"
    //         });
    //         initContainer.VolumeMounts.Add(new V1VolumeMount
    //         {
    //             Name = "sentry-config",
    //             MountPath = "/etc/sentry/requirements.txt",
    //             SubPath = "requirements.txt"
    //         });
    //         initContainer.VolumeMounts.Add(new V1VolumeMount
    //         {
    //             Name = "sentry-config",
    //             MountPath = "/etc/sentry/config.yml",
    //             SubPath = "config.yml"
    //         });
    //         podSpec.InitContainers = new List<V1Container>
    //         {
    //             initContainer
    //         };
    //         container.Args = new List<string>
    //         {
    //             "pip install -r /etc/sentry/requirements.txt && exec /docker-entrypoint.sh run web"
    //         };
    //     }
    //
    //     if (service.Key == "geoipupdate")
    //     {
    //         container.Command = new[] { "/bin/sh", "-ce" };
    //
    //         container.Args = new[]
    //         {
    //             "touch crontab.tmp && echo '0 0 * * * /usr/bin/geoipupdate -d /sentry -f /sentry/GeoIP.conf' > crontab.tmp && crontab crontab.tmp && rm -rf crontab.tmp && /usr/sbin/crond -f -d 0"
    //         };
    //         
    //         container.VolumeMounts.Add(new V1VolumeMount
    //         {
    //             Name = "geoip-conf",
    //             MountPath = "/sentry/GeoIP.conf",
    //             SubPath = "GeoIP.conf"
    //         });
    //         
    //         podSpec.Volumes.Add(new V1Volume("geoip-conf", configMap: new V1ConfigMapVolumeSource(name: "geoip-conf")));
    //     }
    //
    //     return podSpec;
    // }
    //
    // private V1Container GenerateSentryContainer(string name, SentryDeployment sentryDeployment, DockerService service)
    // {
    //     return GenerateContainer(name, sentryDeployment, service, "pip install -r /etc/sentry/requirements.txt && exec /docker-entrypoint.sh ");
    // }
    //
    // private V1Container GenerateSnubaContainer(string name, SentryDeployment sentryDeployment, DockerService service)
    // {
    //     var container = GenerateContainer(name, sentryDeployment, service);
    //
    //     container.EnvFrom ??= new List<V1EnvFromSource>();
    //     container.EnvFrom.Add(new V1EnvFromSource(new V1ConfigMapEnvSource("snuba-env")));
    //     
    //     return container;
    // }
    //
    // private static V1Container GenerateContainer(string name, SentryDeployment sentryDeployment, DockerService service, string commandPrefix = "")
    // {
    //     var testCommands = service.Healthcheck?.Test is string testString
    //         ? new[] { testString }
    //         : (service.Healthcheck?.Test as IEnumerable<object>)?.Select(x => x.ToString()).Where(x => x != "CMD" && x != "CMD-SHELL").ToArray();
    //     return new()
    //     {
    //         Name = name,
    //         Image = service.Image,
    //         Command = commandPrefix == string.Empty ? null : new[] { "sh", "-ce" },
    //         Args = service.Command == null ? null : 
    //             commandPrefix == string.Empty ? ((service.Command as IEnumerable<string>)?.ToArray() ?? (service.Command as string)?.Split(" ")) :
    //             (new string[]
    //             {
    //                 commandPrefix + (service.Command is string s ? s : string.Join(" ", service.Command as IEnumerable<string> ?? Array.Empty<string>()))
    //             }),
    //         Env = service.Environment?.Select(x => new V1EnvVar
    //         {
    //             Name = x.Key,
    //             Value = x.Value
    //         }).ToList(),
    //         Ports = service.Ports?.Select(x => new V1ContainerPort
    //         {
    //             ContainerPort = int.Parse(x.Split(":")[0])
    //         }).ToList(),
    //         VolumeMounts = new List<V1VolumeMount>(),
    //         Resources = new V1ResourceRequirements()
    //         {
    //             Requests = GetRequests(name, sentryDeployment),
    //             Limits = GetLimits(name, sentryDeployment)
    //         },
    //         LivenessProbe = service.Healthcheck != null
    //             ? new V1Probe
    //             {
    //                 Exec = new V1ExecAction
    //                 {
    //                     Command = testCommands
    //                 },
    //                 InitialDelaySeconds = int.Parse(service.Healthcheck.StartPeriod?.Trim('s') ?? "0"),
    //                 PeriodSeconds = int.Parse(service.Healthcheck.Interval?.Trim('s') ?? "0"),
    //                 TimeoutSeconds = int.Parse(service.Healthcheck.Timeout?.Trim('s') ?? "0"),
    //                 FailureThreshold = int.Parse(service.Healthcheck.Retries?.ToString() ?? "0"),
    //             }
    //             : null,
    //         ReadinessProbe = service.Healthcheck != null
    //             ? new V1Probe
    //             {
    //                 Exec = new V1ExecAction
    //                 {
    //                     Command = testCommands
    //                 },
    //                 InitialDelaySeconds = int.Parse(service.Healthcheck.StartPeriod?.Trim('s') ?? "0"),
    //                 PeriodSeconds = int.Parse(service.Healthcheck.Interval?.Trim('s') ?? "0"),
    //                 TimeoutSeconds = int.Parse(service.Healthcheck.Timeout?.Trim('s') ?? "0"),
    //                 FailureThreshold = int.Parse(service.Healthcheck.Retries?.ToString() ?? "0"),
    //             }
    //             : null,
    //     };
    // }
    //
    // private static IDictionary<string, ResourceQuantity>? GetRequests(string name, SentryDeployment sentryDeployment)
    // {
    //     return name switch
    //     {
    //         "web" => sentryDeployment.Spec.Resources?.Web?.Requests ?? null,
    //         "worker" => sentryDeployment.Spec.Resources?.Worker?.Requests ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("100m") }, { "memory", new ResourceQuantity("2Gi") }, },
    //         "cron" => sentryDeployment.Spec.Resources?.Cron?.Requests ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("25m") }, { "memory", new ResourceQuantity("250Mi") }, },
    //         "snuba-api" => sentryDeployment.Spec.Resources?.Snuba?.Requests ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("25m") }, { "memory", new ResourceQuantity("110Mi") }, },
    //         "relay" => sentryDeployment.Spec.Resources?.Relay?.Requests ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("20m") }, { "memory", new ResourceQuantity("500Mi") }, },
    //         _ when name.Contains("consumer") => sentryDeployment.Spec.Resources?.Consumer?.Requests ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("10m") }, { "memory", new ResourceQuantity("200Mi") }, },
    //         _ when name.Contains("ingest") => sentryDeployment.Spec.Resources?.Ingest?.Requests ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("10m") }, { "memory", new ResourceQuantity("200Mi") }, },
    //         _ when name.Contains("forwarder") => sentryDeployment.Spec.Resources?.Forwarder?.Requests ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("10m") }, { "memory", new ResourceQuantity("200Mi") }, },
    //         _ when name.Contains("replacer") => sentryDeployment.Spec.Resources?.Replacer?.Requests ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("10m") }, { "memory", new ResourceQuantity("200Mi") }, },
    //         _ => null
    //     };
    // }
    //
    // private static IDictionary<string, ResourceQuantity>? GetLimits(string name, SentryDeployment sentryDeployment)
    // {
    //     return name switch
    //     {
    //         "web" => sentryDeployment.Spec.Resources?.Web?.Limits ?? null,
    //         "worker" => sentryDeployment.Spec.Resources?.Worker?.Limits ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("200m") }, { "memory", new ResourceQuantity("2.5Gi") }, },
    //         "cron" => sentryDeployment.Spec.Resources?.Cron?.Limits ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("50m") }, { "memory", new ResourceQuantity("1Gi") }, },
    //         "snuba-api" => sentryDeployment.Spec.Resources?.Snuba?.Limits ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("50m") }, { "memory", new ResourceQuantity("200Mi") }, },
    //         "relay" => sentryDeployment.Spec.Resources?.Relay?.Limits ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("100m") }, { "memory", new ResourceQuantity("1Gi") }, },
    //         _ when name.Contains("consumer") => sentryDeployment.Spec.Resources?.Consumer?.Limits ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("50m") }, { "memory", new ResourceQuantity("500Mi") }, },
    //         _ when name.Contains("ingest") => sentryDeployment.Spec.Resources?.Ingest?.Limits ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("50m") }, { "memory", new ResourceQuantity("500Mi") }, },
    //         _ when name.Contains("forwarder") => sentryDeployment.Spec.Resources?.Forwarder?.Limits ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("100m") }, { "memory", new ResourceQuantity("500Mi") }, },
    //         _ when name.Contains("replacer") => sentryDeployment.Spec.Resources?.Replacer?.Limits ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("100m") }, { "memory", new ResourceQuantity("500Mi") }, },
    //         _ when name.Contains("geoip") => sentryDeployment.Spec.Resources?.GeoIP?.Limits ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("100m") }, { "memory", new ResourceQuantity("500Mi") }, },
    //         _ => null
    //     };
    // }
}