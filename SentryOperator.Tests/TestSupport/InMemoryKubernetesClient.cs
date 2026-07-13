using System.Reflection;
using System.Text;
using k8s;
using k8s.Models;
using KubeOps.KubernetesClient;
using KubeOps.KubernetesClient.LabelSelectors;
using Microsoft.Extensions.Logging;

namespace SentryOperator.Tests;

internal sealed record KubernetesOperation(
    int Sequence,
    string Method,
    string ResourceType,
    string Name,
    string? Namespace,
    string? Detail = null);

internal sealed class InMemoryKubernetesClient : IKubernetesClient
{
    private const string RelayCredentialsCommand = "relay credentials generate --stdout";
    private const string RelayCredentialsJson = """{"publicKey":"relay-public","secretKey":"relay-secret"}""";
    private static readonly TimeSpan MinimumDeploymentDuration = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly Dictionary<ResourceKey, string> _resources = [];
    private readonly List<KubernetesOperation> _operations = [];
    private readonly IKubernetes _apiClient;
    private int _sequence;
    private int _resourceVersion;
    private readonly ILogger<InMemoryKubernetesClient> _logger;

    public InMemoryKubernetesClient(ILogger<InMemoryKubernetesClient> logger)
    {
        _logger = logger;
        _apiClient = KubernetesApiClientProxy.Create(ExecutePodCommandAsync);
    }

    public IKubernetes ApiClient => _apiClient;

    public Uri BaseUri { get; } = new("https://local.test");

    public void Dispose()
    {
    }

    public IReadOnlyList<KubernetesOperation> Operations
    {
        get
        {
            lock (_gate)
            {
                return _operations.ToArray();
            }
        }
    }

    public IReadOnlyList<KubernetesOperation> MutationOperations =>
        Operations.Where(operation => operation.Method is "Create" or "Update" or "Delete" or "UpdateStatus")
            .ToArray();

    public void Seed<TEntity>(TEntity entity)
        where TEntity : IKubernetesObject<V1ObjectMeta>
    {
        lock (_gate)
        {
            Upsert(Clone(entity), updateStatus: false);
        }
    }

    public TEntity? GetStored<TEntity>(string name, string namespaceProperty = "default")
        where TEntity : class, IKubernetesObject<V1ObjectMeta>
    {
        lock (_gate)
        {
            return TryGetStored<TEntity>(name, namespaceProperty);
        }
    }

    public IList<TEntity> ListStored<TEntity>(string? namespaceProperty = null)
        where TEntity : class, IKubernetesObject<V1ObjectMeta>
    {
        lock (_gate)
        {
            return GetStoredResources<TEntity>(namespaceProperty, labelSelector: null);
        }
    }

    public IReadOnlyList<string> ExportResourceFingerprints()
    {
        lock (_gate)
        {
            return _resources
                .OrderBy(item => item.Key.Type.Name, StringComparer.Ordinal)
                .ThenBy(item => item.Key.Namespace, StringComparer.Ordinal)
                .ThenBy(item => item.Key.Name, StringComparer.Ordinal)
                .Select(item => $"{item.Key.Type.Name}:{item.Key.Namespace}:{item.Key.Name}:{item.Value}")
                .ToArray();
        }
    }

    public async Task WaitForConditionAsync(
        Func<InMemoryKubernetesClient, bool> condition,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow <= deadline)
        {
            if (condition(this))
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Timed out waiting for in-memory Kubernetes client state.");
    }

    public Task<string> GetCurrentNamespaceAsync(string namespaceFile = null!, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public string GetCurrentNamespace(string namespaceFile = null!) =>
        throw new NotSupportedException();

    public Task<TEntity?> GetAsync<TEntity>(string name, string? @namespace = null, CancellationToken cancellationToken = default)
        where TEntity : IKubernetesObject<V1ObjectMeta>
    {
        cancellationToken.ThrowIfCancellationRequested();
        Log("Get", typeof(TEntity).Name, name, @namespace);
        lock (_gate)
        {
            return Task.FromResult<TEntity?>(TryGetStored<TEntity>(name, @namespace));
        }
    }

    public TEntity? Get<TEntity>(string name, string? @namespace = null)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public Task<IList<TEntity>> ListAsync<TEntity>(
        string? @namespace = null,
        string? labelSelector = null,
        CancellationToken cancellationToken = default)
        where TEntity : IKubernetesObject<V1ObjectMeta>
    {
        cancellationToken.ThrowIfCancellationRequested();
        Log("List", typeof(TEntity).Name, "*", @namespace, labelSelector);
        lock (_gate)
        {
            if (typeof(TEntity) == typeof(V1Pod))
            {
                var pods = BuildSyntheticPods(@namespace, labelSelector);
                return Task.FromResult((IList<TEntity>)pods.Cast<TEntity>().ToList());
            }

            return Task.FromResult((IList<TEntity>)GetStoredResources<TEntity>(@namespace, labelSelector));
        }
    }

    public Task<IList<TEntity>> ListAsync<TEntity>(string? @namespace = null, params LabelSelector[] selectors)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public IList<TEntity> List<TEntity>(string? @namespace = null, string? labelSelector = null)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public IList<TEntity> List<TEntity>(string? @namespace = null, params LabelSelector[] selectors)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public Task<TEntity> SaveAsync<TEntity>(TEntity entity, CancellationToken cancellationToken = default)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public Task<IEnumerable<TEntity>> SaveAsync<TEntity>(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public Task<IEnumerable<TEntity>> SaveAsync<TEntity>(params TEntity[] entities)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public TEntity Save<TEntity>(TEntity entity)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public IEnumerable<TEntity> Save<TEntity>(IEnumerable<TEntity> entities)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public IEnumerable<TEntity> Save<TEntity>(params TEntity[] entities)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public async Task<TEntity> CreateAsync<TEntity>(TEntity entity, CancellationToken cancellationToken = default)
        where TEntity : IKubernetesObject<V1ObjectMeta>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clone = Clone(entity);

        if (IsWorkload(clone))
        {
            return await DeployWorkloadAsync(clone, "Create", cancellationToken);
        }

        lock (_gate)
        {
            var key = ResourceKey.For(clone);
            if (_resources.ContainsKey(key))
            {
                throw new InvalidOperationException($"Resource '{key}' already exists.");
            }

            Upsert(clone, updateStatus: true);
            Log("Create", typeof(TEntity).Name, clone.Metadata?.Name ?? string.Empty, clone.Metadata?.NamespaceProperty);
        }

        return Clone(clone);
    }

    public Task<IEnumerable<TEntity>> CreateAsync<TEntity>(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public Task<IEnumerable<TEntity>> CreateAsync<TEntity>(params TEntity[] entities)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public TEntity Create<TEntity>(TEntity entity)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public IEnumerable<TEntity> Create<TEntity>(IEnumerable<TEntity> entities)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public IEnumerable<TEntity> Create<TEntity>(params TEntity[] entities)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public async Task<TEntity> UpdateAsync<TEntity>(TEntity entity, CancellationToken cancellationToken = default)
        where TEntity : IKubernetesObject<V1ObjectMeta>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clone = Clone(entity);

        if (IsWorkload(clone))
        {
            return await DeployWorkloadAsync(clone, "Update", cancellationToken);
        }

        lock (_gate)
        {
            EnsureExists(clone);
            Upsert(clone, updateStatus: true);
            Log("Update", typeof(TEntity).Name, clone.Metadata?.Name ?? string.Empty, clone.Metadata?.NamespaceProperty);
        }

        return Clone(clone);
    }

    public Task<IEnumerable<TEntity>> UpdateAsync<TEntity>(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public Task<IEnumerable<TEntity>> UpdateAsync<TEntity>(params TEntity[] entities)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public TEntity Update<TEntity>(TEntity entity)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public IEnumerable<TEntity> Update<TEntity>(IEnumerable<TEntity> entities)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public IEnumerable<TEntity> Update<TEntity>(params TEntity[] entities)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public Task<TEntity> UpdateStatusAsync<TEntity>(TEntity entity, CancellationToken cancellationToken = default)
        where TEntity : IKubernetesObject<V1ObjectMeta>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clone = Clone(entity);
        lock (_gate)
        {
            EnsureExists(clone);
            Upsert(clone, updateStatus: true);
            Log("UpdateStatus", typeof(TEntity).Name, clone.Metadata?.Name ?? string.Empty, clone.Metadata?.NamespaceProperty);
        }

        return Task.FromResult(Clone(clone));
    }

    public TEntity UpdateStatus<TEntity>(TEntity entity)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public Task DeleteAsync<TEntity>(TEntity entity, CancellationToken cancellationToken = default)
        where TEntity : IKubernetesObject<V1ObjectMeta>
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Remove(entity);
            Log("Delete", typeof(TEntity).Name, entity.Metadata?.Name ?? string.Empty, entity.Metadata?.NamespaceProperty);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync<TEntity>(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public Task DeleteAsync<TEntity>(params TEntity[] entities)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public Task DeleteAsync<TEntity>(string name, string? @namespace = null, CancellationToken cancellationToken = default)
        where TEntity : IKubernetesObject<V1ObjectMeta>
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _resources.Remove(new ResourceKey(typeof(TEntity), @namespace ?? string.Empty, name));
            Log("Delete", typeof(TEntity).Name, name, @namespace);
        }

        return Task.CompletedTask;
    }

    public void Delete<TEntity>(TEntity entity)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public void Delete<TEntity>(IEnumerable<TEntity> entities)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public void Delete<TEntity>(params TEntity[] entities)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public void Delete<TEntity>(string name, string? @namespace = null)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public Watcher<TEntity> Watch<TEntity>(
        Action<WatchEventType, TEntity> onEvent,
        Action<Exception>? onError = null,
        Action? onClosed = null,
        string? @namespace = null,
        TimeSpan? timeout = null,
        bool? allowBookmarks = null,
        string? resourceVersion = null,
        CancellationToken cancellationToken = default,
        params LabelSelector[] selectors)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public Watcher<TEntity> Watch<TEntity>(
        Action<WatchEventType, TEntity> onEvent,
        Action<Exception>? onError = null,
        Action? onClosed = null,
        string? @namespace = null,
        TimeSpan? timeout = null,
        bool? allowBookmarks = null,
        string? resourceVersion = null,
        string? labelSelector = null,
        CancellationToken cancellationToken = default)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    public IAsyncEnumerable<(WatchEventType Type, TEntity Entity)> WatchAsync<TEntity>(
        string? @namespace = null,
        string? resourceVersion = null,
        string? labelSelector = null,
        bool? allowBookmarks = null,
        CancellationToken cancellationToken = default)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        throw new NotSupportedException();

    private async Task<int> ExecutePodCommandAsync(
        string podName,
        string namespaceProperty,
        string containerName,
        IEnumerable<string> command,
        bool tty,
        ExecAsyncCallback callback,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var commandText = string.Join(" ", command);
        Log("Exec", nameof(V1Pod), podName, namespaceProperty, commandText);
        if (commandText != RelayCredentialsCommand)
        {
            throw new NotSupportedException($"Unsupported pod exec command '{commandText}'.");
        }

        await using var stdout = new MemoryStream(Encoding.UTF8.GetBytes(RelayCredentialsJson));
        await using var stderr = new MemoryStream();
        await callback(Stream.Null, stdout, stderr);
        return 0;
    }

    private void Log(string method, string resourceType, string name, string? namespaceProperty, string? detail = null)
    {
        _logger.LogInformation($"[{++_sequence}] {method} {resourceType} '{name}' in namespace '{namespaceProperty}'{(detail != null ? $" with detail '{detail}'" : string.Empty)}");
        lock (_gate)
        {
            _operations.Add(new KubernetesOperation(_sequence, method, resourceType, name, namespaceProperty, detail));
        }
    }

    private async Task<TEntity> DeployWorkloadAsync<TEntity>(
        TEntity entity,
        string operation,
        CancellationToken cancellationToken)
        where TEntity : IKubernetesObject<V1ObjectMeta>
    {
        var name = entity.Metadata?.Name ?? string.Empty;
        var namespaceProperty = entity.Metadata?.NamespaceProperty;

        lock (_gate)
        {
            if (operation == "Create")
            {
                var key = ResourceKey.For(entity);
                if (_resources.ContainsKey(key))
                {
                    throw new InvalidOperationException($"Resource '{key}' already exists.");
                }
            }
            else
            {
                EnsureExists(entity);
            }

            ApplyPendingReadiness(entity);
            Upsert(entity, updateStatus: false);
            Log("DeployStart", typeof(TEntity).Name, name, namespaceProperty, operation);
            Log(operation, typeof(TEntity).Name, name, namespaceProperty);
        }

        await Task.Delay(MinimumDeploymentDuration, cancellationToken);

        lock (_gate)
        {
            ApplyReadiness(entity);
            Upsert(entity, updateStatus: false);
            Log("DeployComplete", typeof(TEntity).Name, name, namespaceProperty, operation);
        }

        return Clone(entity);
    }

    private static bool IsWorkload<TEntity>(TEntity entity)
        where TEntity : IKubernetesObject<V1ObjectMeta> =>
        entity is V1Deployment or V1StatefulSet;

    private static TEntity Clone<TEntity>(TEntity entity)
        where TEntity : IKubernetesObject<V1ObjectMeta>
    {
        return KubernetesYaml.Deserialize<TEntity>(KubernetesYaml.Serialize(entity), strict: false);
    }

    private TEntity TryGetStored<TEntity>(string name, string? namespaceProperty)
        where TEntity : IKubernetesObject<V1ObjectMeta>
    {
        if (_resources.TryGetValue(new ResourceKey(typeof(TEntity), namespaceProperty ?? string.Empty, name), out var yaml))
        {
            return KubernetesYaml.Deserialize<TEntity>(yaml, strict: false);
        }

        return default!;
    }

    private List<TEntity> GetStoredResources<TEntity>(string? namespaceProperty, string? labelSelector)
        where TEntity : IKubernetesObject<V1ObjectMeta>
    {
        return _resources
            .Where(item => item.Key.Type == typeof(TEntity))
            .Select(item => KubernetesYaml.Deserialize<TEntity>(item.Value, strict: false))
            .Where(item => MatchesNamespace(item.Metadata?.NamespaceProperty, namespaceProperty) &&
                           MatchesLabelSelector(item.Metadata?.Labels, labelSelector))
            .ToList();
    }

    private void Upsert<TEntity>(TEntity entity, bool updateStatus)
        where TEntity : IKubernetesObject<V1ObjectMeta>
    {
        entity.Metadata ??= new V1ObjectMeta();
        entity.Metadata.ResourceVersion = (++_resourceVersion).ToString();

        if (updateStatus)
        {
            ApplyReadiness(entity);
        }

        _resources[ResourceKey.For(entity)] = KubernetesYaml.Serialize(entity);
    }

    private void EnsureExists<TEntity>(TEntity entity)
        where TEntity : IKubernetesObject<V1ObjectMeta>
    {
        if (!_resources.ContainsKey(ResourceKey.For(entity)))
        {
            throw new InvalidOperationException($"Resource '{ResourceKey.For(entity)}' does not exist.");
        }
    }

    private void Remove<TEntity>(TEntity entity)
        where TEntity : IKubernetesObject<V1ObjectMeta>
    {
        _resources.Remove(ResourceKey.For(entity));
    }

    private static void ApplyReadiness<TEntity>(TEntity entity)
        where TEntity : IKubernetesObject<V1ObjectMeta>
    {
        switch (entity)
        {
            case V1Deployment deployment:
                deployment.Status ??= new V1DeploymentStatus();
                deployment.Status.Replicas = deployment.Spec?.Replicas ?? 1;
                deployment.Status.AvailableReplicas = deployment.Spec?.Replicas ?? 1;
                deployment.Status.Conditions =
                [
                    new V1DeploymentCondition
                    {
                        Type = "Available",
                        Status = "True",
                    },
                ];
                break;
            case V1StatefulSet statefulSet:
                statefulSet.Status ??= new V1StatefulSetStatus();
                statefulSet.Status.Replicas = statefulSet.Spec?.Replicas ?? 1;
                statefulSet.Status.ReadyReplicas = statefulSet.Spec?.Replicas ?? 1;
                statefulSet.Status.Conditions =
                [
                    new V1StatefulSetCondition
                    {
                        Type = "Available",
                        Status = "True",
                    },
                ];
                break;
            case V1Pod pod:
                pod.Status ??= new V1PodStatus();
                pod.Status.Phase ??= "Running";
                break;
        }
    }

    private static void ApplyPendingReadiness<TEntity>(TEntity entity)
        where TEntity : IKubernetesObject<V1ObjectMeta>
    {
        switch (entity)
        {
            case V1Deployment deployment:
                deployment.Status ??= new V1DeploymentStatus();
                deployment.Status.Replicas = deployment.Spec?.Replicas ?? 1;
                deployment.Status.AvailableReplicas = 0;
                deployment.Status.Conditions =
                [
                    new V1DeploymentCondition
                    {
                        Type = "Available",
                        Status = "False",
                    },
                ];
                break;
            case V1StatefulSet statefulSet:
                statefulSet.Status ??= new V1StatefulSetStatus();
                statefulSet.Status.Replicas = statefulSet.Spec?.Replicas ?? 1;
                statefulSet.Status.ReadyReplicas = 0;
                statefulSet.Status.Conditions =
                [
                    new V1StatefulSetCondition
                    {
                        Type = "Available",
                        Status = "False",
                    },
                ];
                break;
        }
    }

    private List<V1Pod> BuildSyntheticPods(string? namespaceProperty, string? labelSelector)
    {
        var pods = new List<V1Pod>();
        foreach (var deployment in GetStoredResources<V1Deployment>(namespaceProperty, labelSelector: null))
        {
            pods.Add(CreatePod(
                deployment.Metadata?.Name ?? string.Empty,
                deployment.Metadata?.NamespaceProperty,
                deployment.Metadata?.Labels,
                deployment.Spec?.Template?.Metadata?.Labels,
                deployment.Spec?.Template?.Spec?.Containers ?? [],
                (deployment.Status?.AvailableReplicas ?? 0) > 0));
        }

        foreach (var statefulSet in GetStoredResources<V1StatefulSet>(namespaceProperty, labelSelector: null))
        {
            pods.Add(CreatePod(
                statefulSet.Metadata?.Name ?? string.Empty,
                statefulSet.Metadata?.NamespaceProperty,
                statefulSet.Metadata?.Labels,
                statefulSet.Spec?.Template?.Metadata?.Labels,
                statefulSet.Spec?.Template?.Spec?.Containers ?? [],
                (statefulSet.Status?.ReadyReplicas ?? 0) > 0));
        }

        return pods
            .Where(pod => MatchesNamespace(pod.Metadata?.NamespaceProperty, namespaceProperty) &&
                          MatchesLabelSelector(pod.Metadata?.Labels, labelSelector))
            .ToList();
    }

    private static V1Pod CreatePod(
        string workloadName,
        string? namespaceProperty,
        IDictionary<string, string>? workloadLabels,
        IDictionary<string, string>? podLabels,
        IList<V1Container> containers,
        bool ready)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in workloadLabels ?? new Dictionary<string, string>())
        {
            labels[key] = value;
        }

        foreach (var (key, value) in podLabels ?? new Dictionary<string, string>())
        {
            labels[key] = value;
        }

        return new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = $"{workloadName}-0",
                NamespaceProperty = namespaceProperty,
                Labels = labels,
            },
            Spec = new V1PodSpec
            {
                Containers = containers.Select(container => new V1Container
                {
                    Name = container.Name,
                    Image = container.Image,
                }).ToList(),
            },
            Status = new V1PodStatus
            {
                Phase = ready ? "Running" : "Pending",
            },
        };
    }

    private static bool MatchesNamespace(string? actualNamespace, string? expectedNamespace) =>
        string.IsNullOrWhiteSpace(expectedNamespace) ||
        string.Equals(actualNamespace, expectedNamespace, StringComparison.Ordinal);

    private static bool MatchesLabelSelector(
        IDictionary<string, string>? labels,
        string? labelSelector)
    {
        if (string.IsNullOrWhiteSpace(labelSelector))
        {
            return true;
        }

        foreach (var segment in labelSelector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = segment.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                throw new NotSupportedException($"Unsupported label selector '{labelSelector}'.");
            }

            if (labels == null ||
                !labels.TryGetValue(parts[0], out var actualValue) ||
                !string.Equals(actualValue, parts[1], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct ResourceKey(Type Type, string Namespace, string Name)
    {
        public static ResourceKey For<TEntity>(TEntity entity)
            where TEntity : IKubernetesObject<V1ObjectMeta>
        {
            var metadata = entity.Metadata ?? throw new InvalidOperationException("Kubernetes resource metadata is required.");
            return new ResourceKey(entity.GetType(), metadata.NamespaceProperty ?? string.Empty, metadata.Name ?? string.Empty);
        }

        public override string ToString() => $"{Type.Name}/{Namespace}/{Name}";
    }
}

internal class KubernetesApiClientProxy : DispatchProxy
{
    private Func<string, string, string, IEnumerable<string>, bool, ExecAsyncCallback, CancellationToken, Task<int>> _executePodCommandAsync = null!;

    public static IKubernetes Create(
        Func<string, string, string, IEnumerable<string>, bool, ExecAsyncCallback, CancellationToken, Task<int>> executePodCommandAsync)
    {
        var proxy = DispatchProxy.Create<IKubernetes, KubernetesApiClientProxy>();
        ((KubernetesApiClientProxy)(object)proxy)._executePodCommandAsync = executePodCommandAsync;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IKubernetes.NamespacedPodExecAsync))
        {
            return _executePodCommandAsync(
                (string)args![0]!,
                (string)args[1]!,
                (string)args[2]!,
                (IEnumerable<string>)args[3]!,
                (bool)args[4]!,
                (ExecAsyncCallback)args[5]!,
                (CancellationToken)args[6]!);
        }

        throw new NotSupportedException($"Unsupported IKubernetes member '{targetMethod?.Name}'.");
    }
}
