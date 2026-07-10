using SentryOperator.Docker.Compose;
using SentryOperator.Docker.Converters;

namespace SentryOperator.Docker;

public class ContainerConverterResolver : IContainerConverterResolver
{
    private readonly IEnumerable<IDockerContainerConverter> _converters;

    public ContainerConverterResolver(IEnumerable<IDockerContainerConverter> converters)
    {
        _converters = converters;
    }

    public IDockerContainerConverter Resolve(string serviceName, DockerService service)
    {
        return _converters
            .Where(converter => converter.CanConvert(serviceName, service))
            .OrderByDescending(converter => converter.Priority)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No container converter is registered for Compose service '{serviceName}'.");
    }
}
