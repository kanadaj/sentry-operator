using SentryOperator.Entities;

namespace SentryOperator.Services;

public interface IDefaultConfigProvisioner
{
    Task EnsureAsync(SentryDeployment entity);

    Task GenerateRelayCredentials(SentryDeployment entity);
}
