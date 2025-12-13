using k8s;
using k8s.Models;
using SentryOperator.Docker.Converters;
using SentryOperator.Entities;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NodeDeserializers;

namespace SentryOperator.Docker;

public class DockerComposeConverter
{
    private readonly IEnumerable<IDockerContainerConverter> _converters;

    /// <summary>
    /// These services should be managed by the user and not by the operator because they require external resources and tuning.
    /// An external operator may be used for ease of use. For this reason, this operator will not manage these services.
    /// </summary>
    private readonly string[] _ignoredServices = new[]
    {
        "smtp",
        //"memcached",
        "redis",
        "postgres",
        "clickhouse",
        "zookeeper",
        "kafka",
        "nginx",
        "seaweedfs", // We currently do not support this service, and on Kubernetes, Ceph or Minio are far more mature options anyway
    };

    private readonly ILogger _logger;

    public DockerComposeConverter(ILogger<DockerComposeConverter> logger, IEnumerable<IDockerContainerConverter> converters)
    {
        _logger = logger;
        _converters = converters;
    }

    public List<IKubernetesObject<V1ObjectMeta>> Convert(string dockerComposeYaml, SentryDeployment sentryDeployment)
    {
        var dockerCompose = Parse(dockerComposeYaml, sentryDeployment.Spec.DockerComposeOverrides);
        
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

    public DockerCompose Parse(string dockerComposeYaml, string? overrides)
    {
        var mergingParser = new MergingParser(new Parser(new StringReader(dockerComposeYaml)));
        var deserializer = new DeserializerBuilder()
            .WithNodeDeserializer(inner => new ArrayAsDictionaryNodeDeserializer(inner), syntax => syntax.InsteadOf<DictionaryNodeDeserializer>())
            .IgnoreUnmatchedProperties()
            .Build();
        
        var dockerComposeFile = deserializer
            .Deserialize<DockerCompose>(mergingParser);

        if (overrides != null)
        {
            var mergedParser = new MergingParser(new Parser(new StringReader(dockerComposeYaml+"\n"+overrides)));

            var overridesData = deserializer.Deserialize<DockerCompose>(mergedParser);

            if (overridesData.Services != null)
            {
                foreach (var service in overridesData.Services)
                {
                    if (dockerComposeFile.Services == null)
                    {
                        dockerComposeFile.Services = new Dictionary<string, DockerService>();
                    }

                    dockerComposeFile.Services[service.Key] = service.Value;
                }
            }
        }

        return dockerComposeFile;
    }
}