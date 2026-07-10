using System.Text;
using k8s;
using k8s.Models;
using SentryOperator.Docker.Compose;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public class TaskWorkerConverter : SentryContainerConverter
{
    public override int Priority => 1;
    public override bool CanConvert(string name, DockerService service) => name == "taskworker";

    public override IEnumerable<IKubernetesObject<V1ObjectMeta>> Convert(string name, DockerService service,
        SentryDeployment sentryDeployment)
    {
        foreach (var resource in base.Convert(name, service, sentryDeployment))
        {
            yield return resource;
        }

        var autoscaling = sentryDeployment.Spec.Config?.TaskWorkerAutoscaling;
        if (autoscaling != null)
        {
            yield return CreateHorizontalPodAutoscaler(name, sentryDeployment, autoscaling);
        }
    }

    protected override IKubernetesObject<V1ObjectMeta> CreateDeployment(string name, DockerService service,
        SentryDeployment sentryDeployment)
    {
        var deployment = (V1Deployment)base.CreateDeployment(name, service, sentryDeployment);
        var autoscaling = sentryDeployment.Spec.Config?.TaskWorkerAutoscaling;
        if (autoscaling != null)
        {
            deployment.Spec.Replicas = autoscaling.MinReplicas;
        }

        return deployment;
    }

    protected override V1Container GetBaseContainer(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        var container = base.GetBaseContainer(name, service, sentryDeployment);

        var args = container.Args[0].Replace("$SENTRY_TASKWORKER_CONCURRENCY", "4").Split(" ").ToList();
        
        if(sentryDeployment.Spec.Config?.TaskWorkerConcurrency is not null and not 4)
        {
            var arg = args.FirstOrDefault(x => x.Contains("--concurrency"));
            if (arg != null)
            {
                args.Remove(arg);
            }
            args.Add($"--concurrency={sentryDeployment.Spec.Config.TaskWorkerConcurrency}");
        }

        if ((sentryDeployment.Spec.Replicas?.TryGetValue("taskbroker", out var brokerCount) ?? false) && brokerCount > 1)
        {
            // We remove the rpc-host option and instead build a rpc-host-list from 0 to N-1 using taskbroker-N.taskbroker:50051
            var arg = args.FirstOrDefault(x => x.Contains("--rpc-host"));
            if (arg != null)
            {
                args.Remove(arg);
            }

            var option = new StringBuilder("--rpc-host-list=");
            for (int i = 0; i < brokerCount; i++)
            {
                option.Append("taskbroker-").Append(i).Append(".taskbroker:50051").Append(",");
            }
            option.Remove(option.Length - 1, 1);
            args.Add(option.ToString());
        }
        container.Args[0] = string.Join(" ", args);

        return container;
    }

    private static V2HorizontalPodAutoscaler CreateHorizontalPodAutoscaler(string name,
        SentryDeployment sentryDeployment, TaskWorkerAutoscaling autoscaling)
    {
        return new V2HorizontalPodAutoscaler
        {
            ApiVersion = "autoscaling/v2",
            Kind = "HorizontalPodAutoscaler",
            Metadata = new V1ObjectMeta
            {
                Name = $"{name}-autoscaler",
                NamespaceProperty = sentryDeployment.Namespace(),
                Labels = new Dictionary<string, string>
                {
                    { "app.kubernetes.io/name", name },
                    { "app.kubernetes.io/instance", name },
                    { "app.kubernetes.io/version", sentryDeployment.Spec.GetVersion() },
                    { "app.kubernetes.io/managed-by", "sentry-operator" },
                }
            },
            Spec = new V2HorizontalPodAutoscalerSpec
            {
                MinReplicas = autoscaling.MinReplicas,
                MaxReplicas = autoscaling.MaxReplicas,
                ScaleTargetRef = new V2CrossVersionObjectReference
                {
                    ApiVersion = "apps/v1",
                    Kind = "Deployment",
                    Name = name,
                },
                Metrics =
                [
                    new V2MetricSpec
                    {
                        Type = "Resource",
                        Resource = new V2ResourceMetricSource
                        {
                            Name = "cpu",
                            Target = new V2MetricTarget
                            {
                                Type = "Utilization",
                                AverageUtilization = autoscaling.CpuTargetUtilization,
                            }
                        }
                    },
                    new V2MetricSpec
                    {
                        Type = "External",
                        External = new V2ExternalMetricSource
                        {
                            Metric = new V2MetricIdentifier { Name = autoscaling.ExternalMetricName },
                            Target = new V2MetricTarget
                            {
                                Type = "AverageValue",
                                AverageValue = new ResourceQuantity(autoscaling.ConsumerLagTarget.ToString()),
                            }
                        }
                    }
                ]
            }
        };
    }
}