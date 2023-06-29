﻿using k8s.Models;
using SentryOperator.Entities;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace SentryOperator.Docker;

internal class DockerComposeConverter
{
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

    public (V1Deployment[] Deployments, V1Service[] Services) Convert(string dockerComposeYaml, SentryDeployment sentryDeployment, string version = "nightly")
    {
        var dockerCompose = Parse(dockerComposeYaml);
        var deployments = new List<V1Deployment>();

        foreach (var service in dockerCompose.Services!)
        {
            if (_ignoredServices.Contains(service.Key)) continue;
            
            if (service.Value.Image == "sentry-self-hosted-local" || (service.Value.Image?.StartsWith("getsentry/sentry:") ?? true))
            {
                service.Value.Image = $"getsentry/sentry:{version}";
            }
            else if (service.Value.Image == "$SNUBA_IMAGE" || (service.Value.Image?.StartsWith("getsentry/snuba:") ?? true))
            {
                service.Value.Image = $"getsentry/snuba:{version}";
            }

            var deployment = new V1Deployment
            {
                ApiVersion = "apps/v1",
                Kind = "Deployment",
                Metadata = new V1ObjectMeta
                {
                    Name = service.Key,
                    NamespaceProperty = sentryDeployment.Namespace(),
                    Labels = new Dictionary<string, string>
                    {
                        { "app.kubernetes.io/name", service.Key },
                        { "app.kubernetes.io/instance", service.Key },
                        { "app.kubernetes.io/version", version },
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
                            { "app.kubernetes.io/name", service.Key },
                            { "app.kubernetes.io/instance", service.Key },
                        }
                    },
                    Replicas = (sentryDeployment.Spec.Replicas?.TryGetValue(service.Key, out var replicas) ?? false) ? replicas : 1,
                    Template = new V1PodTemplateSpec
                    {
                        Metadata = new V1ObjectMeta
                        {
                            Labels = new Dictionary<string, string>
                            {
                                { "app.kubernetes.io/name", service.Key },
                                { "app.kubernetes.io/instance", service.Key },
                            }
                        },
                        Spec = GeneratePodSpec(service, sentryDeployment)
                    }
                }
            };
            
            deployments.Add(deployment);
        }
        
        var services = new List<V1Service>();

        /*
         * Pods that need services:
         * web: 9000
         * snuba-api: 1218
         * symbolicator: 3021
         * relay: 3000
         */
        foreach (var (service, port) in new Dictionary<string, int>
                 {
                        { "web", 9000 },
                        { "snuba-api", 1218 },
                        { "symbolicator", 3021 },
                        { "relay", 3000 },
                        { "vroom", 8085 },
                 })
        {
            var svc = new V1Service()
            {
                Metadata = new V1ObjectMeta
                {
                    Name = service,
                    NamespaceProperty = sentryDeployment.Namespace(),
                    Labels = new Dictionary<string, string>
                    {
                        { "app.kubernetes.io/name", service },
                        { "app.kubernetes.io/instance", service },
                        { "app.kubernetes.io/version", version },
                        { "app.kubernetes.io/managed-by", "sentry-operator" },
                    }
                },
                Spec = new V1ServiceSpec
                {
                    Ports = new List<V1ServicePort>()
                    {
                        new V1ServicePort
                        {
                            Name = service,
                            Port = port,
                            TargetPort = port,
                        }
                    }
                }
            };
            services.Add(svc);
        }
        
        return (deployments.ToArray(), services.ToArray());
    }

    private V1PodSpec GeneratePodSpec(KeyValuePair<string, DockerService> service, SentryDeployment sentryDeployment)
    {
        var container = service.Value.Image!.Contains("snuba", StringComparison.OrdinalIgnoreCase) ? GenerateSnubaContainer(service.Key, sentryDeployment, service.Value) : GenerateSentryContainer(service.Key, sentryDeployment, service.Value);
        var podSpec = new V1PodSpec
        {
            Containers = new List<V1Container>
            {
                container
            },
            Volumes = new List<V1Volume>()
            {
                
            }
        };

        container.EnvFrom = new List<V1EnvFromSource>
        {
            new V1EnvFromSource
            {
                SecretRef = new V1SecretEnvSource("sentry-env")
            }
        };

        if (sentryDeployment.Spec.Environment != null)
        {
            foreach (var env in container.Env)
            {
                if(sentryDeployment.Spec.Environment.TryGetValue(env.Name, out var value))
                {
                    env.Value = value;
                } 
            }   
        }
        
        foreach(var volumeObject in service.Value.Volumes?.AsEnumerable() ?? Array.Empty<object>())
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
                container.VolumeMounts.Add(volumeMount);
                podSpec.Volumes.Add(new V1Volume(volumeName, secret: new V1SecretVolumeSource(420, secretName: "sentry-certificates")));
                continue;
            }
            
            volumeMount = new V1VolumeMount
            {
                Name = volumeName,
                MountPath = volumePath
            };
            container.VolumeMounts.Add(volumeMount);
            podSpec.Volumes.Add(new V1Volume(volumeName, persistentVolumeClaim: new V1PersistentVolumeClaimVolumeSource(volumeName)));
        }
        

        if (service.Value.Image!.Contains("sentry", StringComparison.OrdinalIgnoreCase))
        {
            podSpec.Volumes.Add(new V1Volume("sentry-config", secret:  new V1SecretVolumeSource(420, optional: false, secretName: "sentry-config")));

            // We don't mount to /data if it's already taken
            if (container.VolumeMounts.All(x => x.MountPath != "/data"))
            {
                container.VolumeMounts.Add(new V1VolumeMount
                {
                    Name = "sentry-data",
                    MountPath = "/data"
                });
                podSpec.Volumes.Add(new V1Volume("sentry-data", persistentVolumeClaim:new V1PersistentVolumeClaimVolumeSource("sentry-data")));
            }
            
            container.VolumeMounts.Add(new V1VolumeMount
            {
                Name = "sentry-config",
                MountPath = "/etc/sentry/sentry.conf.py",
                SubPath = "sentry.conf.py"
            });
            container.VolumeMounts.Add(new V1VolumeMount
            {
                Name = "sentry-config",
                MountPath = "/etc/sentry/requirements.txt",
                SubPath = "requirements.txt"
            });
            container.VolumeMounts.Add(new V1VolumeMount
            {
                Name = "sentry-config",
                MountPath = "/etc/sentry/config.yml",
                SubPath = "config.yml"
            });
        }
        
        if (service.Key == "web")
        {
            var initContainer = GenerateSentryContainer("db-setup", sentryDeployment, new DockerService()
            {
                Image = container.Image,
                Command = new[]{"upgrade --noinput"},
            });
            initContainer.EnvFrom = new List<V1EnvFromSource>
            {
                new V1EnvFromSource
                {
                    SecretRef = new V1SecretEnvSource("sentry-env")
                }
            };
        }
        
        return podSpec;
    }

    private V1Container GenerateSentryContainer(string name, SentryDeployment sentryDeployment, DockerService service)
    {
        return GenerateContainer(name, sentryDeployment, service, "pip install -r /etc/sentry/requirements.txt && exec /docker-entrypoint.sh");
    }

    private V1Container GenerateSnubaContainer(string name, SentryDeployment sentryDeployment, DockerService service)
    {
        return GenerateContainer(name, sentryDeployment, service);
    }

    private static V1Container GenerateContainer(string name, SentryDeployment sentryDeployment, DockerService service, string commandPrefix = "")
    {
        var testCommands = service.Healthcheck?.Test is string testString
            ? new[] { testString }
            : (service.Healthcheck?.Test as IEnumerable<object>)?.Select(x => x.ToString()).Where(x => x != "CMD" && x != "CMD-SHELL").ToArray();
        return new()
        {
            Name = name,
            Image = service.Image,
            Command = new[] { "/bin/bash", "-c" },
            Args = (new string[]
            {
                commandPrefix
            }.Concat((service.Command is string s ? new[]{s} : service.Command as IEnumerable<string>) ?? Array.Empty<string>()).ToArray()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray(),
            Env = service.Environment?.Select(x => new V1EnvVar
            {
                Name = x.Key,
                Value = x.Value
            }).ToList(),
            Ports = service.Ports?.Select(x => new V1ContainerPort
            {
                ContainerPort = int.Parse(x.Split(":")[0])
            }).ToList(),
            VolumeMounts = new List<V1VolumeMount>(),
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
    }

    private static IDictionary<string, ResourceQuantity>? GetRequests(string name, SentryDeployment sentryDeployment)
    {
        return name switch
        {
            "web" => sentryDeployment.Spec.Resources?.Web?.Requests ?? null,
            "worker" => sentryDeployment.Spec.Resources?.Worker?.Requests ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("100m") }, { "memory", new ResourceQuantity("2Gi") }, },
            "cron" => sentryDeployment.Spec.Resources?.Cron?.Requests ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("25m") }, { "memory", new ResourceQuantity("250Mi") }, },
            "snuba-api" => sentryDeployment.Spec.Resources?.Snuba?.Requests ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("25m") }, { "memory", new ResourceQuantity("110Mi") }, },
            "relay" => sentryDeployment.Spec.Resources?.Relay?.Requests ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("20m") }, { "memory", new ResourceQuantity("500Mi") }, },
            _ when name.Contains("consumer") => sentryDeployment.Spec.Resources?.Consumer?.Requests ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("10m") }, { "memory", new ResourceQuantity("200Mi") }, },
            _ when name.Contains("ingest") => sentryDeployment.Spec.Resources?.Ingest?.Requests ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("10m") }, { "memory", new ResourceQuantity("200Mi") }, },
            _ when name.Contains("forwarder") => sentryDeployment.Spec.Resources?.Forwarder?.Requests ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("10m") }, { "memory", new ResourceQuantity("200Mi") }, },
            _ when name.Contains("replacer") => sentryDeployment.Spec.Resources?.Replacer?.Requests ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("10m") }, { "memory", new ResourceQuantity("200Mi") }, },
            _ => null
        };
    }
    
    private static IDictionary<string, ResourceQuantity>? GetLimits(string name, SentryDeployment sentryDeployment)
    {
        return name switch
        {
            "web" => sentryDeployment.Spec.Resources?.Web?.Limits ?? null,
            "worker" => sentryDeployment.Spec.Resources?.Worker?.Limits ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("200m") }, { "memory", new ResourceQuantity("2.5Gi") }, },
            "cron" => sentryDeployment.Spec.Resources?.Cron?.Limits ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("50m") }, { "memory", new ResourceQuantity("1Gi") }, },
            "snuba-api" => sentryDeployment.Spec.Resources?.Snuba?.Limits ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("50m") }, { "memory", new ResourceQuantity("200Mi") }, },
            "relay" => sentryDeployment.Spec.Resources?.Relay?.Limits ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("100m") }, { "memory", new ResourceQuantity("1Gi") }, },
            _ when name.Contains("consumer") => sentryDeployment.Spec.Resources?.Consumer?.Limits ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("50m") }, { "memory", new ResourceQuantity("500Mi") }, },
            _ when name.Contains("ingest") => sentryDeployment.Spec.Resources?.Ingest?.Limits ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("50m") }, { "memory", new ResourceQuantity("500Mi") }, },
            _ when name.Contains("forwarder") => sentryDeployment.Spec.Resources?.Forwarder?.Limits ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("100m") }, { "memory", new ResourceQuantity("500Mi") }, },
            _ when name.Contains("replacer") => sentryDeployment.Spec.Resources?.Replacer?.Limits ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("100m") }, { "memory", new ResourceQuantity("500Mi") }, },
            _ when name.Contains("geoip") => sentryDeployment.Spec.Resources?.GeoIP?.Limits ?? new Dictionary<string, ResourceQuantity> { { "cpu", new ResourceQuantity("100m") }, { "memory", new ResourceQuantity("500Mi") }, },
            _ => null
        };
    }

    public DockerCompose Parse(string dockerComposeYaml)
    {
        var mergingParser = new MergingParser(new Parser(new StringReader(dockerComposeYaml)));
        var dockerComposeFile = new DeserializerBuilder()
            .Build()
            .Deserialize<DockerCompose>(mergingParser);

        return dockerComposeFile;
    }
}