using SentryOperator.Entities;

namespace SentryOperator.Services;

public interface ICertificateProvisioner
{
    Task<bool> EnsureAsync(SentryDeployment entity);
}
