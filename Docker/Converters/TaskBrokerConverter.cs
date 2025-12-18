using k8s;
using k8s.Models;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public class TaskBrokerConverter : ContainerConverter
{
    public override bool CanConvert(string name, DockerService service) => name == "taskbroker";

    protected override V1Container GetBaseContainer(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        var container = base.GetBaseContainer(name, service, sentryDeployment);
        container.Ports ??= new List<V1ContainerPort>();
        container.Ports.Add(new V1ContainerPort
        {
            ContainerPort = 50051,
            Name = "taskbroker"
        });
        
        return container;
    }

    protected override IEnumerable<V1Volume> GetVolumes(DockerService service, SentryDeployment sentryDeployment)
    {
        var vols = base.GetVolumes(service, sentryDeployment);
        foreach(var volume in vols)
        {
            if (volume.Name == "sentry-taskbroker")
            {
                
            }
            else
            {
                yield return volume;
            }
        }
    }

    protected override IKubernetesObject<V1ObjectMeta> CreateDeployment(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        return new V1StatefulSet
        {
            ApiVersion = "apps/v1",
            Kind = "StatefulSet",
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
            Spec = new V1StatefulSetSpec
            {
                UpdateStrategy = new V1StatefulSetUpdateStrategy()
                {
                    Type = "RollingUpdate",
                    RollingUpdate = new V1RollingUpdateStatefulSetStrategy()
                },
                Selector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>
                    {
                        { "app.kubernetes.io/name", name },
                        { "app.kubernetes.io/instance", name },
                    }
                },
                VolumeClaimTemplates = new List<V1PersistentVolumeClaim>()
                {
                    new V1PersistentVolumeClaim(spec: new V1PersistentVolumeClaimSpec(volumeName: "sentry-taskbroker", resources:new V1VolumeResourceRequirements(requests: new Dictionary<string, ResourceQuantity>()
                    {
                        ["storage"] = new ResourceQuantity("5Gi")
                    })))
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
}