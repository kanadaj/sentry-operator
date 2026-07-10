using SentryOperator.Entities;

namespace SentryOperator.Services;

public interface IComposeSourceResolver
{
    string GetUrl(SentryDeployment deployment);
    Task<string> GetComposeAsync(SentryDeployment deployment, CancellationToken cancellationToken = default);
}
