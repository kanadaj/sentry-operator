using System.Runtime.Serialization;

namespace SentryOperator.Docker.Compose;

public enum ServiceCondition
{
    [EnumMember(Value = "service_started")]
    ServiceStarted,

    [EnumMember(Value = "service_healthy")]
    ServiceHealthy,

    [EnumMember(Value = "service_completed_successfully")]
    ServiceCompletedSuccessfully
}