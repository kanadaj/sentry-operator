using k8s.Models;
using KubeOps.KubernetesClient;
using SentryOperator.Entities;

namespace SentryOperator.Services;

public class ManagedResourceCleanup : IManagedResourceCleanup
{
    private readonly IKubernetesClient _client;

    public ManagedResourceCleanup(IKubernetesClient client)
    {
        _client = client;
    }

    public async Task CleanupAsync(SentryDeployment entity, CancellationToken cancellationToken)
    {
    var deployments = await _client.ListAsync<V1Deployment>(entity.Namespace(), labelSelector: $"app.kubernetes.io/managed-by=sentry-operator",
        cancellationToken: cancellationToken);
    foreach (var deployment in deployments)
    {
        await _client.DeleteAsync(deployment, CancellationToken.None);
    }

    var horizontalPodAutoscalers = await _client.ListAsync<V2HorizontalPodAutoscaler>(entity.Namespace(),
        labelSelector: "app.kubernetes.io/managed-by=sentry-operator", cancellationToken: cancellationToken);
    foreach (var horizontalPodAutoscaler in horizontalPodAutoscalers)
    {
        await _client.DeleteAsync(horizontalPodAutoscaler, CancellationToken.None);
    }

    var services = await _client.ListAsync<V1Service>(entity.Namespace(), labelSelector: $"app.kubernetes.io/managed-by=sentry-operator", CancellationToken.None);
    foreach (var service in services)
    {
        await _client.DeleteAsync(service, CancellationToken.None);
    }

    var configMaps = await _client.ListAsync<V1ConfigMap>(entity.Namespace(), labelSelector: $"app.kubernetes.io/managed-by=sentry-operator", CancellationToken.None);
    foreach (var configMap in configMaps)
    {
        await _client.DeleteAsync(configMap, CancellationToken.None);
    }

    var secrets = await _client.ListAsync<V1Secret>(entity.Namespace(), labelSelector: $"app.kubernetes.io/managed-by=sentry-operator", CancellationToken.None);
    foreach (var secret in secrets)
    {
        await _client.DeleteAsync(secret, CancellationToken.None);
    }

    var certName = entity.Name() + "-certificate";
    var certificate = await _client.GetAsync<Certificate>(certName, entity.Namespace(), CancellationToken.None);
    if (certificate != null)
    {
        await _client.DeleteAsync(certificate, CancellationToken.None);
    }
}

}
