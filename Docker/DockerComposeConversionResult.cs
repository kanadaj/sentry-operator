using k8s;
using k8s.Models;
using SentryOperator.Docker.Compose;

namespace SentryOperator.Docker;

/// <summary>
/// Represents the result of converting a docker-compose file to Kubernetes resources.
/// Includes both the resources and metadata needed for orchestrating deployment order.
/// </summary>
public class DockerComposeConversionResult
{
    public required List<IKubernetesObject<V1ObjectMeta>> Resources { get; set; }
    public required DockerCompose DockerCompose { get; set; }
    public required DeploymentOrchestrator Orchestrator { get; set; }
}
