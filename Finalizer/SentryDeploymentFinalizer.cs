using k8s.Models;
using KubeOps.Abstractions.Finalizer;
using SentryOperator.Entities;

namespace SentryOperator.Finalizer;

public class SentryDeploymentFinalizer : IEntityFinalizer<SentryDeployment>
{
    private readonly ILogger<SentryDeploymentFinalizer> _logger;

    public SentryDeploymentFinalizer(ILogger<SentryDeploymentFinalizer> logger)
    {
        _logger = logger;
    }

    public Task FinalizeAsync(SentryDeployment entity, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Entity {Name} called {FinalizeAsyncName}", entity.Name(), nameof(FinalizeAsync));

        return Task.CompletedTask;
    }
}
