using System.Text.RegularExpressions;
using k8s;
using k8s.Models;
using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Events;
using KubeOps.Abstractions.Finalizer;
using KubeOps.Abstractions.Rbac;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Caching.Distributed;
using SentryOperator.Docker;
using SentryOperator.Entities;
using SentryOperator.Extensions;
using SentryOperator.Finalizer;
using SentryOperator.Services;

namespace SentryOperator.Controller;

[EntityRbac(typeof(SentryDeployment), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Pod), Verbs = RbacVerb.Create | RbacVerb.Delete | RbacVerb.Patch | RbacVerb.Update | RbacVerb.Get | RbacVerb.List)]
[EntityRbac(typeof(V1Deployment), Verbs = RbacVerb.Create | RbacVerb.Delete | RbacVerb.Patch | RbacVerb.Update | RbacVerb.Get | RbacVerb.List)]
[EntityRbac(typeof(V1StatefulSet), Verbs = RbacVerb.Create | RbacVerb.Delete | RbacVerb.Patch | RbacVerb.Update | RbacVerb.Get | RbacVerb.List)]
[EntityRbac(typeof(V1Service), Verbs = RbacVerb.Create | RbacVerb.Delete | RbacVerb.Patch | RbacVerb.Update | RbacVerb.Get | RbacVerb.List)]
[EntityRbac(typeof(V2HorizontalPodAutoscaler), Verbs = RbacVerb.Create | RbacVerb.Delete | RbacVerb.Patch | RbacVerb.Update | RbacVerb.Get | RbacVerb.List)]
[EntityRbac(typeof(V1Secret), Verbs = RbacVerb.Create | RbacVerb.Delete | RbacVerb.Patch | RbacVerb.Update | RbacVerb.Get | RbacVerb.List)]
[EntityRbac(typeof(V1ConfigMap), Verbs = RbacVerb.Create | RbacVerb.Delete | RbacVerb.Patch | RbacVerb.Update | RbacVerb.Get | RbacVerb.List)]
[GenericRbac(Resources = new[] { "certificates" }, Groups = new[] { "cert-manager.io" }, Verbs = RbacVerb.Get | RbacVerb.Delete | RbacVerb.Patch | RbacVerb.Create)]
public class SentryDeploymentController : IEntityController<SentryDeployment>
{
    private readonly ILogger<SentryDeploymentController> _logger;
    private readonly EntityFinalizerAttacher<SentryDeploymentFinalizer, SentryDeployment> _finalizer;
    private readonly RemoteFileService _remoteFileService;
    private readonly ICertificateProvisioner _certificateProvisioner;
    private readonly IDefaultConfigProvisioner _defaultConfigProvisioner;
    private readonly IManagedResourceCleanup _managedResourceCleanup;
    private readonly IComposeSourceResolver _composeSourceResolver;
    private readonly IKubernetesClient _client;
    private readonly DockerComposeConverter _dockerComposeConverter;
    private readonly IHorizontalPodAutoscalerReconciler _horizontalPodAutoscalerReconciler;

    public SentryDeploymentController(ILogger<SentryDeploymentController> logger, EntityFinalizerAttacher<SentryDeploymentFinalizer, SentryDeployment> finalizer,
        RemoteFileService remoteFileService, ICertificateProvisioner certificateProvisioner, IDefaultConfigProvisioner defaultConfigProvisioner,
        IManagedResourceCleanup managedResourceCleanup, IComposeSourceResolver composeSourceResolver, DockerComposeConverter dockerComposeConverter,
        IKubernetesClient client, IHorizontalPodAutoscalerReconciler horizontalPodAutoscalerReconciler)
    {
        _logger = logger;
        _finalizer = finalizer;
        _remoteFileService = remoteFileService;
        _certificateProvisioner = certificateProvisioner;
        _defaultConfigProvisioner = defaultConfigProvisioner;
        _managedResourceCleanup = managedResourceCleanup;
        _composeSourceResolver = composeSourceResolver;
        _client = client;
        _dockerComposeConverter = dockerComposeConverter;
        _horizontalPodAutoscalerReconciler = horizontalPodAutoscalerReconciler;
    }

    public async Task ReconcileAsync(SentryDeployment entity, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Entity {Name} called {ReconcileAsyncName}", entity.Name(), nameof(ReconcileAsync));
        await _finalizer(entity, cancellationToken);

        var conversionResult = await FetchAndConvertDockerComposeWithOrchestrator(entity, cancellationToken);
        var resources = conversionResult.Resources;
        var orchestrator = conversionResult.Orchestrator;

        await _defaultConfigProvisioner.EnsureAsync(entity);

        // We don't need to query the resources if the status has the same checksum as the generated spec
        var configChecksum = resources.GetChecksum();
        _logger.LogInformation("Entity {Name} expected checksum: {Checksum}, actual checksum: {ActualChecksum}", entity.Name(), configChecksum, entity.Status.LastVersion);
        if (entity.Status.LastVersion == configChecksum && configChecksum != null)
        {
            return;
        }

        var services = resources.OfType<V1Service>().ToList();
        var deployments = resources.OfType<V1Deployment>().ToList();
        var statefulSets = resources.OfType<V1StatefulSet>().ToList();
        var horizontalPodAutoscalers = resources.OfType<V2HorizontalPodAutoscaler>().ToList();

        var actualServices = await _client.ListAsync<V1Service>(entity.Namespace(), cancellationToken: cancellationToken);
        var actualDeployments = await _client.ListAsync<V1Deployment>(entity.Namespace(), cancellationToken: cancellationToken);
        var actualStatefulSets = await _client.ListAsync<V1StatefulSet>(entity.Namespace(), cancellationToken: cancellationToken);

        if (!CheckIfUpdateIsNeeded(services, actualServices, deployments, actualDeployments, statefulSets, actualStatefulSets, entity) &&
            !await _horizontalPodAutoscalerReconciler.NeedsUpdateAsync(horizontalPodAutoscalers, entity, cancellationToken))
        {
            if (entity.Status.Status != "Ready" || string.IsNullOrWhiteSpace(entity.Status.LastVersion))
            {
                entity.Status.Status = "Ready";
                entity.Status.Message = "Sentry deployment is ready";
                entity.Status.LastVersion = configChecksum;
                await _client.UpdateStatusAsync(entity, CancellationToken.None);
            }

            return;
        }

        entity.Status.Status = "Updating";
        entity.Status.Message = "Updating Sentry deployment";
        await _client.UpdateStatusAsync(entity, CancellationToken.None);

        // Deploy services first (they have no dependencies and may be needed by deployments/statefulsets)
        foreach (var service in services)
        {
            var checksum = service.GetChecksum();
            service.SetLabel("sentry-operator/checksum", checksum);
            var svc = actualServices.FirstOrDefault(s => s.Name() == service.Name());
            if (svc == null)
            {
                service.AddOwnerReference(entity.MakeOwnerReference());
                await _client.CreateAsync(service, CancellationToken.None);
            }
            else if (svc.GetLabel("app.kubernetes.io/managed-by") == "sentry-operator")
            {
                if (svc.GetLabel("sentry-operator/checksum") != checksum)
                {
                    service.Metadata.ResourceVersion = svc.Metadata.ResourceVersion;
                    service.AddOwnerReference(entity.MakeOwnerReference());
                    await _client.UpdateAsync(service, CancellationToken.None);
                }
            }
        }

        // Deploy deployments and statefulsets in dependency order with health checks
        await DeployWorkloadResourcesInOrder(
            deployments, statefulSets, actualDeployments, actualStatefulSets, 
            orchestrator, entity, cancellationToken);
        await _horizontalPodAutoscalerReconciler.ReconcileAsync(horizontalPodAutoscalers, entity, cancellationToken);

        foreach (var deployment in actualDeployments)
        {
            if (deployments.All(d => d.Name() != deployment.Name()) && deployment.GetLabel("app.kubernetes.io/managed-by") == "sentry-operator")
            {
                await _client.DeleteAsync(deployment, CancellationToken.None);
            }
        }

        foreach (var statefulSet in actualStatefulSets)
        {
            if (statefulSets.All(s => s.Name() != statefulSet.Name()) && statefulSet.GetLabel("app.kubernetes.io/managed-by") == "sentry-operator")
            {
                await _client.DeleteAsync(statefulSet, CancellationToken.None);
            }
        }

        if ((entity.Spec.Certificate?.Install ?? true))
        {
            var result = await _certificateProvisioner.EnsureAsync(entity);
            if (!result)
            {
                _logger.LogInformation("Requeuing event");

                entity.Status.Status = "Error creating certificate";
                await _client.UpdateStatusAsync(entity, CancellationToken.None);

                return;
            }
        }

        entity = (await _client.GetAsync<SentryDeployment>(entity.Name(), entity.Namespace(), CancellationToken.None))!;
        entity.Status.Status = "Ready";
        entity.Status.Message = "Sentry deployment is ready";
        entity.Status.LastVersion = configChecksum;
        await _client.UpdateStatusAsync(entity, CancellationToken.None);
        //return ResourceControllerResult.RequeueEvent(TimeSpan.FromSeconds(15));
        return;
    }

    private async Task DeployWorkloadResourcesInOrder(
        List<V1Deployment> deployments,
        List<V1StatefulSet> statefulSets,
        IList<V1Deployment> actualDeployments,
        IList<V1StatefulSet> actualStatefulSets,
        DeploymentOrchestrator orchestrator,
        SentryDeployment entity,
        CancellationToken cancellationToken)
    {
        var allWorkloads = new Dictionary<string, object>();
        foreach (var d in deployments)
            allWorkloads[d.Name()] = d;
        foreach (var s in statefulSets)
            allWorkloads[s.Name()] = s;

        var deploymentOrder = orchestrator.CalculateDeploymentOrder();
        var deployed = new HashSet<string>();

        foreach (var serviceName in deploymentOrder)
        {
            if (!allWorkloads.ContainsKey(serviceName))
                continue;

            var workload = allWorkloads[serviceName];

            // Wait for dependencies to be ready before deploying
            while (!orchestrator.AreDependenciesSatisfied(serviceName, actualDeployments, actualStatefulSets))
            {
                _logger.LogInformation("Waiting for dependencies of {ServiceName} to be ready", serviceName);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                
                // Refresh deployment status
                actualDeployments = await _client.ListAsync<V1Deployment>(entity.Namespace(), cancellationToken: cancellationToken);
                actualStatefulSets = await _client.ListAsync<V1StatefulSet>(entity.Namespace(), cancellationToken: cancellationToken);
            }

            _logger.LogInformation("Deploying {ServiceName} with dependencies satisfied", serviceName);

            if (workload is V1Deployment deployment)
            {
                await DeployWorkload(deployment, actualDeployments, entity, cancellationToken);
                deployed.Add(serviceName);
                actualDeployments = await _client.ListAsync<V1Deployment>(entity.Namespace(), cancellationToken: cancellationToken);
            }
            else if (workload is V1StatefulSet statefulSet)
            {
                await DeployStatefulSet(statefulSet, actualStatefulSets, entity, cancellationToken);
                deployed.Add(serviceName);
                actualStatefulSets = await _client.ListAsync<V1StatefulSet>(entity.Namespace(), cancellationToken: cancellationToken);
            }
        }
    }

    private async Task DeployWorkload(
        V1Deployment deployment,
        IList<V1Deployment> actualDeployments,
        SentryDeployment entity,
        CancellationToken cancellationToken)
    {
        var checksum = deployment.GetChecksum();
        deployment.SetLabel("sentry-operator/checksum", checksum);
        var actualDeployment = actualDeployments.FirstOrDefault(d => d.Name() == deployment.Name());

        if (actualDeployment == null)
        {
            deployment.AddOwnerReference(entity.MakeOwnerReference());
            await _client.CreateAsync(deployment, CancellationToken.None);
            _logger.LogInformation("Created deployment {DeploymentName}", deployment.Name());
        }
        else if (actualDeployment.GetLabel("app.kubernetes.io/managed-by") == "sentry-operator")
        {
            _logger.LogDebug("Checking deployment {DeploymentName} expected checksum: {DeploymentChecksum}, actual checksum: {ActualChecksum}",
                deployment.Name(), checksum, actualDeployment.GetLabel("sentry-operator/checksum"));

            if (actualDeployment.GetLabel("sentry-operator/checksum") != checksum || entity.Spec.Version == "nightly")
            {
                _logger.LogInformation("Updating deployment {DeploymentName}", deployment.Name());
                deployment.Metadata.ResourceVersion = actualDeployment.Metadata.ResourceVersion;
                deployment.AddOwnerReference(entity.MakeOwnerReference());
                await _client.UpdateAsync(deployment, CancellationToken.None);
            }
        }
    }

    private async Task DeployStatefulSet(
        V1StatefulSet statefulSet,
        IList<V1StatefulSet> actualStatefulSets,
        SentryDeployment entity,
        CancellationToken cancellationToken)
    {
        var checksum = statefulSet.GetChecksum();
        statefulSet.SetLabel("sentry-operator/checksum", checksum);
        var actualStatefulSet = actualStatefulSets.FirstOrDefault(s => s.Name() == statefulSet.Name());

        if (actualStatefulSet == null)
        {
            statefulSet.AddOwnerReference(entity.MakeOwnerReference());
            await _client.CreateAsync(statefulSet, CancellationToken.None);
            _logger.LogInformation("Created statefulset {StatefulSetName}", statefulSet.Name());
        }
        else if (actualStatefulSet.GetLabel("app.kubernetes.io/managed-by") == "sentry-operator")
        {
            _logger.LogDebug("Checking statefulset {StatefulSetName} expected checksum: {StatefulSetChecksum}, actual checksum: {ActualChecksum}",
                statefulSet.Name(), checksum, actualStatefulSet.GetLabel("sentry-operator/checksum"));

            if (actualStatefulSet.GetLabel("sentry-operator/checksum") != checksum)
            {
                _logger.LogInformation("Updating statefulset {StatefulSetName}", statefulSet.Name());
                statefulSet.Metadata.ResourceVersion = actualStatefulSet.Metadata.ResourceVersion;
                statefulSet.AddOwnerReference(entity.MakeOwnerReference());
                try
                {
                    await _client.UpdateAsync(statefulSet, CancellationToken.None);
                }
                catch (Exception e)
                {
                    // We are trying to change something that can't be changed on StatefulSet update, so we need to delete and recreate it
                    _logger.LogError(e, "Error updating statefulset {StatefulSetName}, deleting and recreating it", statefulSet.Name());
                    await _client.DeleteAsync(actualStatefulSet, CancellationToken.None);
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    statefulSet.AddOwnerReference(entity.MakeOwnerReference());
                    await _client.CreateAsync(statefulSet, CancellationToken.None);
                }
            }
        }
    }

    private bool CheckIfUpdateIsNeeded(List<V1Service> services, 
        IList<V1Service> actualServices, 
        List<V1Deployment> deployments, 
        IList<V1Deployment> actualDeployments,
        List<V1StatefulSet> statefulSets,
        IList<V1StatefulSet> actualStatefulSets,
        SentryDeployment entity)
    {
        foreach (var service in services)
        {
            service.AddOwnerReference(entity.MakeOwnerReference());

            var matchingService = actualServices.FirstOrDefault(s => s.Name() == service.Name());
            if (matchingService == null)
            {
                return true;
            }

            if (matchingService.GetLabel("app.kubernetes.io/managed-by") != "sentry-operator")
            {
                continue;
            }

            _logger.LogInformation("Service {ServiceName} expected checksum: {ServiceChecksum}, actual checksum: {ActualChecksum}", service.Name(), service.GetChecksum(),
                matchingService.GetLabel("sentry-operator/checksum"));

            if (matchingService.GetLabel("sentry-operator/checksum") != service.GetChecksum())
            {
                return true;
            }
        }

        foreach (var deployment in deployments)
        {
            deployment.AddOwnerReference(entity.MakeOwnerReference());

            var matchingDeployment = actualDeployments.FirstOrDefault(d => d.Name() == deployment.Name());

            if (matchingDeployment == null) return true;

            if (matchingDeployment.GetLabel("app.kubernetes.io/managed-by") != "sentry-operator")
            {
                continue;
            }

            _logger.LogInformation("Deployment {DeploymentName} expected checksum: {DeploymentChecksum}, actual checksum: {ActualChecksum}", deployment.Name(),
                deployment.GetChecksum(),
                matchingDeployment.GetLabel("sentry-operator/checksum"));

            if (matchingDeployment.GetLabel("sentry-operator/checksum") != deployment.GetChecksum())
            {
                return true;
            }
        }

        foreach (var statefulSet in statefulSets)
        {
            statefulSet.AddOwnerReference(entity.MakeOwnerReference());
            
            var matchingStatefulSet = actualStatefulSets.FirstOrDefault(s => s.Name() == statefulSet.Name());
            if (matchingStatefulSet == null) return true;
            
            if (matchingStatefulSet.GetLabel("app.kubernetes.io/managed-by") != "sentry-operator")
            {
                continue;
            }
            
            _logger.LogInformation("StatefulSet {StatefulSetName} expected checksum: {StatefulSetChecksum}, actual checksum: {ActualChecksum}", statefulSet.Name(), statefulSet.GetChecksum(),
                matchingStatefulSet.GetLabel("sentry-operator/checksum"));

            if (matchingStatefulSet.GetLabel("sentry-operator/checksum") != statefulSet.GetChecksum())
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> InstallKafkaTopics(SentryDeployment entity)
    {
        const string bitnamiCommand = "/opt/bitnami/kafka/bin/kafka-topics.sh --create --bootstrap-server kafka:9092 --topic ";
        const string kafkaTopicsDefault = "ingest-attachments ingest-transactions ingest-events ingest-replay-recordings profiles ingest-occurrences";
        var kafkaTopicsScriptUrl = $"https://raw.githubusercontent.com/getsentry/self-hosted/{entity.Spec.GetVersion()}/install/create-kafka-topics.sh";

        string kafkaTopics;
        try
        {
            var kafkaTopicsScriptRaw = await _remoteFileService.GetAsync(kafkaTopicsScriptUrl);

            // Find the line that starts with 'NEEDED_KAFKA_TOPICS=' and extract the topics surrounded by a "
            var topicsRegex = new Regex(@"NEEDED_KAFKA_TOPICS=""(.*)""");
            var topicsMatch = topicsRegex.Match(kafkaTopicsScriptRaw);
            kafkaTopics = topicsMatch.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(kafkaTopics))
            {
                kafkaTopics = kafkaTopicsDefault;
            }
        }
        catch
        {
            kafkaTopics = kafkaTopicsDefault;
        }

        var topics = kafkaTopics.Split(" ");

        var pods = await _client.ListAsync<V1Pod>(labelSelector: "app.kubernetes.io/name=kafka");
        var image = pods.FirstOrDefault()?.Spec.Containers.First().Image;

        if (image?.Contains("bitnami") ?? false)
        {
            var cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
            foreach (var topic in topics)
            {
                await _client.ApiClient.NamespacedPodExecAsync(
                    pods.First().Name(),
                    pods.First().Namespace(),
                    pods.First().Spec.Containers.First().Name,
                    new List<string>
                    {
                        bitnamiCommand + topic
                    }, false, async (@in, @out, err) =>
                    {
                        using var sr = new StreamReader(@out);
                        using var srErr = new StreamReader(err);
                        var output = await sr.ReadToEndAsync(cancellationToken);
                        var error = await srErr.ReadToEndAsync(cancellationToken);
                        if (!string.IsNullOrWhiteSpace(error))
                        {
                            _logger.LogError("Error creating Kafka topics: {Error}", error);
                            return;
                        }

                        _logger.LogInformation("Kafka topics created: {Output}", output);
                    }, cancellationToken);
            }

            return true;
        }
        else return false;
    }

    public Task StatusModifiedAsync(SentryDeployment entity)
    {
        _logger.LogInformation("Entity {Name} called {StatusModifiedAsyncName}", entity.Name(), nameof(StatusModifiedAsync));

        return Task.CompletedTask;
    }

    public async Task DeletedAsync(SentryDeployment entity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation("Entity {Name} called {DeletedAsyncName}", entity.Name(), nameof(DeletedAsync));
        await _managedResourceCleanup.CleanupAsync(entity, cancellationToken);
    }

    public Task GenerateRelayCredentials(SentryDeployment entity) =>
        _defaultConfigProvisioner.GenerateRelayCredentials(entity);

    private async Task<DockerComposeConversionResult> FetchAndConvertDockerComposeWithOrchestrator(
        SentryDeployment entity,
        CancellationToken cancellationToken)
    {
        var dockerComposeRaw = await _composeSourceResolver.GetComposeAsync(entity, cancellationToken);
        return _dockerComposeConverter.ConvertWithOrchestrator(dockerComposeRaw, entity);
    }
}

public class ConfigTemplate
{
    public required string Config { get; set; }
    public required string Entrypoint { get; set; }
    public required string SentryConfPy { get; set; }
}
