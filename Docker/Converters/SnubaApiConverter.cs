using k8s.Models;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public class SnubaApiConverter : ContainerConverter
{
    public override bool CanConvert(string name, DockerService service) => name == "snuba-api";

    protected override V1PodSpec GeneratePodSpec(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        var podSpec = base.GeneratePodSpec(name, service, sentryDeployment);

        podSpec.InitContainers = GetInitContainers(service, sentryDeployment);
        
        return podSpec;
    }

    private IList<V1Container> GetInitContainers(DockerService service, SentryDeployment sentryDeployment)
    {
        var bootstrapContainer = GetBaseContainer("snuba-bootstrap", new DockerService()
        {
            Image = service.Image,
        }, sentryDeployment);
            
        bootstrapContainer.Args = new List<string>
        {
            "bootstrap",
            "--force",
            "--no-migrate"
        };
            
        var migrationContainer = GetBaseContainer("snuba-migration", new DockerService()
        {
            Image = service.Image,
        }, sentryDeployment);
            
        migrationContainer.Args = new List<string>
        {
            "migrations",
            "migrate",
            "--force"
        };
            
        return new List<V1Container>
        {
            bootstrapContainer,
            migrationContainer
        };
    }

    protected override V1Container GetBaseContainer(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        var container = base.GetBaseContainer(name, service, sentryDeployment);
        
        container.EnvFrom ??= new List<V1EnvFromSource>();
        container.EnvFrom.Add(new V1EnvFromSource
        {
            SecretRef = new V1SecretEnvSource("sentry-env")
        });
        
        return container;
    }
}