using k8s;
using k8s.Models;
using SentryOperator.Docker.Compose;
using SentryOperator.Docker.Converters;
using SentryOperator.Entities;

namespace SentryOperator.Docker;

public class DockerComposeConverter
{
    /// <summary>
    /// These services should be managed by the user and not by the operator because they require external resources and tuning.
    /// An external operator may be used for ease of use. For this reason, this operator will not manage these services.
    /// </summary>
    public static readonly string[] IgnoredServices = SentryManagedServicePolicy.ExternallyManagedServiceNames;

    private readonly ILogger _logger;
    private readonly IDockerComposeParser _parser;
    private readonly IContainerConverterResolver _converterResolver;
    private readonly IManagedServicePolicy _managedServicePolicy;

    public DockerComposeConverter(ILogger<DockerComposeConverter> logger, IEnumerable<IDockerContainerConverter> converters)
        : this(
            logger,
            new YamlDockerComposeParser(),
            new ContainerConverterResolver(converters),
            new SentryManagedServicePolicy())
    {
    }

    public DockerComposeConverter(
        ILogger<DockerComposeConverter> logger,
        IDockerComposeParser parser,
        IContainerConverterResolver converterResolver,
        IManagedServicePolicy managedServicePolicy)
    {
        _logger = logger;
        _parser = parser;
        _converterResolver = converterResolver;
        _managedServicePolicy = managedServicePolicy;
    }

    public List<IKubernetesObject<V1ObjectMeta>> Convert(string dockerComposeYaml, SentryDeployment sentryDeployment)
    {
        var result = ConvertWithOrchestrator(dockerComposeYaml, sentryDeployment);
        return result.Resources;
    }

    public DockerComposeConversionResult ConvertWithOrchestrator(string dockerComposeYaml, SentryDeployment sentryDeployment)
    {
        var dockerCompose = Parse(dockerComposeYaml, sentryDeployment.Spec.DockerComposeOverrides);
        
        var result = new List<IKubernetesObject<V1ObjectMeta>>();
        foreach (var service in dockerCompose.Services!)
        {
            if (_managedServicePolicy.IsExternallyManaged(service.Key))
            {
                _logger.LogInformation("Ignoring service {ServiceName}", service.Key);
                continue;
            }

            _logger.LogInformation("Converting service {ServiceName}", service.Key);
            var converter = _converterResolver.Resolve(service.Key, service.Value);

            var resources = converter.Convert(service.Key, service.Value, sentryDeployment).ToList();
            foreach (var resource in resources)
            {
                _logger.LogInformation("Converted {ResourceType} {ResourceName}", resource.Kind, resource.Metadata.Name);
            }

            result.AddRange(resources);
        }

        var orchestrator = new DeploymentOrchestrator(_logger, dockerCompose, _managedServicePolicy);
        orchestrator.MapResourcesToServices(result);
        orchestrator.CalculateDeploymentOrder();

        return new DockerComposeConversionResult
        {
            Resources = result,
            DockerCompose = dockerCompose,
            Orchestrator = orchestrator
        };
    }

    public DockerCompose Parse(string dockerComposeYaml, string? overrides)
    {
        return _parser.Parse(dockerComposeYaml, overrides);
    }
}