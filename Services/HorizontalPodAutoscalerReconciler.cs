using k8s.Models;
using KubeOps.Abstractions.Entities;
using KubeOps.KubernetesClient;
using SentryOperator.Entities;
using SentryOperator.Extensions;

namespace SentryOperator.Services;

public class HorizontalPodAutoscalerReconciler : IHorizontalPodAutoscalerReconciler
{
    private const string ChecksumLabel = "sentry-operator/checksum";
    private const string ManagedByLabel = "app.kubernetes.io/managed-by";
    private readonly IKubernetesClient _client;

    public HorizontalPodAutoscalerReconciler(IKubernetesClient client)
    {
        _client = client;
    }

    public async Task<bool> NeedsUpdateAsync(IList<V2HorizontalPodAutoscaler> desired, SentryDeployment entity,
        CancellationToken cancellationToken)
    {
        var actual = await _client.ListAsync<V2HorizontalPodAutoscaler>(entity.Namespace(), cancellationToken: cancellationToken);
        ApplyChecksums(desired);

        if (desired.Any(hpa => actual.All(existing => existing.Name() != hpa.Name())))
        {
            return true;
        }

        return actual.Any(existing => existing.GetLabel(ManagedByLabel) == "sentry-operator" &&
                                      desired.All(hpa => hpa.Name() != existing.Name())) ||
               desired.Any(hpa =>
               {
                   var existing = actual.FirstOrDefault(item => item.Name() == hpa.Name());
                   return existing?.GetLabel(ManagedByLabel) == "sentry-operator" &&
                          existing.GetLabel(ChecksumLabel) != hpa.GetLabel(ChecksumLabel);
               });
    }

    public async Task ReconcileAsync(IList<V2HorizontalPodAutoscaler> desired, SentryDeployment entity,
        CancellationToken cancellationToken)
    {
        var actual = await _client.ListAsync<V2HorizontalPodAutoscaler>(entity.Namespace(), cancellationToken: cancellationToken);
        ApplyChecksums(desired);

        foreach (var hpa in desired)
        {
            var existing = actual.FirstOrDefault(item => item.Name() == hpa.Name());
            if (existing == null)
            {
                hpa.AddOwnerReference(entity.MakeOwnerReference());
                await _client.CreateAsync(hpa, cancellationToken);
            }
            else if (existing.GetLabel(ManagedByLabel) == "sentry-operator" &&
                     existing.GetLabel(ChecksumLabel) != hpa.GetLabel(ChecksumLabel))
            {
                hpa.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                hpa.AddOwnerReference(entity.MakeOwnerReference());
                await _client.UpdateAsync(hpa, cancellationToken);
            }
        }

        foreach (var hpa in actual.Where(item => item.GetLabel(ManagedByLabel) == "sentry-operator" &&
                                                 desired.All(expected => expected.Name() != item.Name())))
        {
            await _client.DeleteAsync(hpa, cancellationToken);
        }
    }

    private static void ApplyChecksums(IEnumerable<V2HorizontalPodAutoscaler> horizontalPodAutoscalers)
    {
        foreach (var hpa in horizontalPodAutoscalers)
        {
            hpa.SetLabel(ChecksumLabel, hpa.GetChecksum());
        }
    }
}
