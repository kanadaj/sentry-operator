using k8s.Models;
using SentryOperator.Docker.Volume;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public class SentryWebConverter : SentryContainerConverter
{
    public override int Priority => 1;
    public override bool CanConvert(string name, DockerService service) => name == "web";
    
    protected override IEnumerable<VolumeRef> GetVolumeData(DockerService service, SentryDeployment sentryDeployment)
    {
        var knownNames = new List<string>();
        foreach(var volume in base.GetVolumeData(service, sentryDeployment))
        {
            if(knownNames.Contains(volume.Name)) continue;
            yield return volume;
            knownNames.Add(volume.Name);
        }
    }
    
    protected override V1PodSpec GeneratePodSpec(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        var podSpec = base.GeneratePodSpec(name, service, sentryDeployment);

        podSpec.InitContainers = GetInitContainers(service, sentryDeployment);
        
        podSpec.Containers[0].Args = new List<string>
        {
            "pip install -r /etc/sentry/requirements.txt && exec /docker-entrypoint.sh run web"
        };

        podSpec.Containers[0].ReadinessProbe = new V1Probe
        {
            HttpGet = new V1HTTPGetAction
            {
                Port = 9000,
                Path = "/_health/",
            },
            InitialDelaySeconds = 3
        };
        
        podSpec.Containers[0].LivenessProbe = new V1Probe
        {
            HttpGet = new V1HTTPGetAction
            {
                Port = 9000,
                Path = "/_health/"
            },
            InitialDelaySeconds = 3
        };
        
        return podSpec;
    }

    private IList<V1Container> GetInitContainers(DockerService service, SentryDeployment sentryDeployment)
    {
        var initContainer = GetBaseContainer("init-db", new DockerService()
        {
            Image = service.Image,
        }, sentryDeployment);
            
        initContainer.Args = new List<string>
        {
            "pip install -r /etc/sentry/requirements.txt && exec /docker-entrypoint.sh upgrade --noinput",
        };
            
        return new List<V1Container>
        {
            initContainer
        };
    }
}