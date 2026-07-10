using SentryOperator.Entities;

namespace SentryOperator.Services;

public class ComposeSourceResolver : IComposeSourceResolver
{
    public const string DefaultDockerComposeUrl =
        "https://raw.githubusercontent.com/getsentry/self-hosted/master/docker-compose.yml";

    private readonly RemoteFileService _remoteFileService;

    public ComposeSourceResolver(RemoteFileService remoteFileService)
    {
        _remoteFileService = remoteFileService;
    }

    public string GetUrl(SentryDeployment deployment)
    {
        if (!string.IsNullOrWhiteSpace(deployment.Spec.DockerComposeUrl))
        {
            return deployment.Spec.DockerComposeUrl;
        }

        if (!string.IsNullOrWhiteSpace(deployment.Spec.Version))
        {
            var version = deployment.Spec.Version == "nightly" ? "master" : deployment.Spec.Version;
            return $"https://raw.githubusercontent.com/getsentry/self-hosted/{version}/docker-compose.yml";
        }

        return DefaultDockerComposeUrl;
    }

    public async Task<string> GetComposeAsync(
        SentryDeployment deployment,
        CancellationToken cancellationToken = default)
    {
        var composeYaml = await _remoteFileService.GetAsync(GetUrl(deployment));
        return (deployment.Spec.Config ?? new SentryDeploymentConfig())
            .ReplaceVariables(composeYaml, deployment.Spec.Version ?? "nightly");
    }
}
