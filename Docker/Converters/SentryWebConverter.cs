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
        
        return podSpec;
    }

    protected override V1Container GetBaseContainer(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        var container = base.GetBaseContainer(name, service, sentryDeployment);
        
        container.Args = new List<string>
        {
            "pip install -r /etc/sentry/requirements.txt && exec /docker-entrypoint.sh run web"
        };

        var healthcheckIntervalString = sentryDeployment.Spec.Config?.HealthCheckInterval;
        int healthcheckInterval = healthcheckIntervalString != null ? int.Parse(healthcheckIntervalString.Trim('s')) : 30;
        var healthcheckTimeoutString = sentryDeployment.Spec.Config?.HealthCheckTimeout;
        int healthcheckTimeout = healthcheckTimeoutString != null ? int.Parse(healthcheckTimeoutString.Trim('s')) : 60;
        var healthcheckRetriesString = sentryDeployment.Spec.Config?.HealthCheckRetries;
        int healthcheckRetries = healthcheckRetriesString != null ? int.Parse(healthcheckRetriesString) : 10;
        var healthcheckStartPeriodString = sentryDeployment.Spec.Config?.HealthCheckStartPeriod;
        int healthcheckStartPeriod = healthcheckStartPeriodString != null ? int.Parse(healthcheckStartPeriodString.Trim('s')) : 10;
        
        container.Ports ??= new List<V1ContainerPort>();
        container.Ports.Add(new V1ContainerPort(9000));

        container.ReadinessProbe = new V1Probe
        {
            HttpGet = new V1HTTPGetAction
            {
                Port = 9000,
                Path = "/_health/",
            },
            InitialDelaySeconds = healthcheckStartPeriod,
            PeriodSeconds = healthcheckInterval,
            TimeoutSeconds = healthcheckTimeout,
            FailureThreshold = healthcheckRetries,
            SuccessThreshold = 1,
        };
        
        container.LivenessProbe = new V1Probe
        {
            HttpGet = new V1HTTPGetAction
            {
                Port = 9000,
                Path = "/_health/"
            },
            InitialDelaySeconds = healthcheckStartPeriod,
            PeriodSeconds = healthcheckInterval,
            TimeoutSeconds = healthcheckTimeout,
            FailureThreshold = healthcheckRetries,
            SuccessThreshold = 1,
        };

        return container;
    }

    private IList<V1Container> GetInitContainers(DockerService service, SentryDeployment sentryDeployment)
    {
        var initContainer = GetBaseContainer("init-db", new DockerService()
        {
            Image = service.Image,
        }, sentryDeployment);
            
        initContainer.Args = new List<string>
        {
            "pip install -r /etc/sentry/requirements.txt && exec /docker-entrypoint.sh upgrade --noinput --create-kafka-topics",
        };
            
        return new List<V1Container>
        {
            initContainer
        };
    }
}