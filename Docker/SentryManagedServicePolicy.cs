namespace SentryOperator.Docker;

public class SentryManagedServicePolicy : IManagedServicePolicy
{
    public static readonly string[] ExternallyManagedServiceNames =
    [
        "smtp",
        "redis",
        "postgres",
        "clickhouse",
        "zookeeper",
        "kafka",
        "nginx",
        "seaweedfs",
    ];

    private static readonly HashSet<string> ExternallyManagedServices =
        new(ExternallyManagedServiceNames);

    public bool IsExternallyManaged(string serviceName) =>
        ExternallyManagedServices.Contains(serviceName);
}
