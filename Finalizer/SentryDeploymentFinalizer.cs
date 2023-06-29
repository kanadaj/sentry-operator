using k8s.Models;
using KubeOps.Operator.Finalizer;
using SentryOperator.Entities;

namespace SentryOperator.Finalizer;

public class SentryDeploymentFinalizer : IResourceFinalizer<SentryDeployment>
{
    private readonly ILogger<SentryDeploymentFinalizer> _logger;

    public SentryDeploymentFinalizer(ILogger<SentryDeploymentFinalizer> logger)
    {
        _logger = logger;
    }

    public Task FinalizeAsync(SentryDeployment entity)
    {
        _logger.LogInformation("Entity {Name} called {FinalizeAsyncName}", entity.Name(), nameof(FinalizeAsync));

        return Task.CompletedTask;
    }
}
