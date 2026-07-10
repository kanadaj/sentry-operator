using SentryOperator.Entities;

namespace SentryOperator.Services;

public interface IManagedResourceCleanup
{
    Task CleanupAsync(SentryDeployment entity, CancellationToken cancellationToken);
}
