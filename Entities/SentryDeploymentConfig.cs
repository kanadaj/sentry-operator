using KubeOps.Abstractions.Entities.Attributes;

namespace SentryOperator.Entities;

public class SentryDeploymentConfig
{
    [Description("The amount of days to keep events for")]
    public int EventRetentionDays { get; set; } = 90;
    
    [Description("You can either use a port number or an IP:PORT combo for SENTRY_BIND")]
    public string Bind { get; set; } = "9000";
    
    [Description("The image to use for Sentry")]
    public string? Image { get; set; }
    
    [Description("The image to use for Snuba")]
    public string? SnubaImage { get; set; }
    
    [Description("The image to use for Relay")]
    public string? RelayImage { get; set; }
    
    [Description("The image to use for Taskbroker")]
    public string? TaskbrokerImage { get; set; }
    
    [Description("The image to use for Symbolicator")]
    public string? SymbolicatorImage { get; set; }
    
    [Description("The image to use for Vroom")]
    public string? VroomImage { get; set; }
    
    [Description("The version of Wal2Json to use")]
    public string Wal2JsonVersion { get; set; } = "latest";
    
    [Description("How much to delay starting health checks")]
    public string HealthCheckStartPeriod { get; set; } = "10s";
    
    [Description("The interval to use for health checks")]
    public string HealthCheckInterval { get; set; } = "30s";
    
    [Description("The timeout to use for health checks")]
    public string HealthCheckTimeout { get; set; } = "90s";
    
    [Description("The amount of retries to use for health checks")]
    public string HealthCheckRetries { get; set; } = "10";
    
    [Description("How much to delay starting health checks")]
    public string HealthCheckFileStartPeriod { get; set; } = "600s";
    
    [Description("The interval to use for health checks")]
    public string HealthCheckFileInterval { get; set; } = "60s";
    
    [Description("The timeout to use for health checks")]
    public string HealthCheckFileTimeout { get; set; } = "10s";
    
    [Description("The amount of retries to use for health checks")]
    public string HealthCheckFileRetries { get; set; } = "3";
    
    [Description("The config for the Postgres database")]
    public PostgresConfig? Postgres { get; set; }
    
    [Description("The config for redis")]
    public RedisConfig[]? Redis { get; set; }
        
    [Description("Additional feature flags to enable")]
    public string[]? AdditionalFeatureFlags { get; set; }
    
    [Description("Additional Python packages to install")]
    public string[]? AdditionalPythonPackages { get; set; }

    [Description("The config for mail")]
    public MailConfig? Mail { get; set; }

    public string ReplaceVariables(string yaml, string version)
    {
        return yaml.Replace("$SENTRY_EVENT_RETENTION_DAYS", EventRetentionDays.ToString())
            .Replace("$SENTRY_BIND", Bind)
            .Replace("$SENTRY_IMAGE", Image ?? $"getsentry/sentry:{version}")
            .Replace("$SNUBA_IMAGE", SnubaImage ?? $"getsentry/snuba:{version}")
            .Replace("$RELAY_IMAGE", RelayImage ?? $"getsentry/relay:{version}")
            .Replace("$TASKBROKER_IMAGE", TaskbrokerImage ?? $"getsentry/taskbroker:{version}")
            .Replace("$SYMBOLICATOR_IMAGE", SymbolicatorImage ?? $"getsentry/symbolicator:{version}")
            .Replace("$VROOM_IMAGE", VroomImage ?? $"getsentry/vroom:{version}")
            .Replace("$WAL2JSON_VERSION", Wal2JsonVersion)
            .Replace("$HEALTHCHECK_START_PERIOD", HealthCheckStartPeriod)
            .Replace("$HEALTHCHECK_INTERVAL", HealthCheckInterval)
            .Replace("$HEALTHCHECK_TIMEOUT", HealthCheckTimeout)
            .Replace("$HEALTHCHECK_RETRIES", HealthCheckRetries)
            .Replace("$HEALTHCHECK_FILE_START_PERIOD", HealthCheckFileStartPeriod)
            .Replace("$HEALTHCHECK_FILE_INTERVAL", HealthCheckFileInterval)
            .Replace("$HEALTHCHECK_FILE_TIMEOUT", HealthCheckFileTimeout)
            .Replace("$HEALTHCHECK_FILE_RETRIES", HealthCheckFileRetries);
    }
}