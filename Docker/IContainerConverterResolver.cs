using SentryOperator.Docker.Compose;
using SentryOperator.Docker.Converters;

namespace SentryOperator.Docker;

public interface IContainerConverterResolver
{
    IDockerContainerConverter Resolve(string serviceName, DockerService service);
}
