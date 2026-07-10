using SentryOperator.Docker.Compose;

namespace SentryOperator.Docker;

public interface IDockerComposeParser
{
    DockerCompose Parse(string dockerComposeYaml, string? overrides);
}
