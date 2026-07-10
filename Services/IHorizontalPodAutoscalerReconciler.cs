using k8s.Models;
using SentryOperator.Entities;

namespace SentryOperator.Services;

public interface IHorizontalPodAutoscalerReconciler
{
    Task<bool> NeedsUpdateAsync(IList<V2HorizontalPodAutoscaler> desired, SentryDeployment entity,
        CancellationToken cancellationToken);

    Task ReconcileAsync(IList<V2HorizontalPodAutoscaler> desired, SentryDeployment entity,
        CancellationToken cancellationToken);
}
