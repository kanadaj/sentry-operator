namespace SentryOperator.Docker;

public interface IManagedServicePolicy
{
    bool IsExternallyManaged(string serviceName);
}
