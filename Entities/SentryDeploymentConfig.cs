using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using KubeOps.Abstractions.Entities.Attributes;

namespace SentryOperator.Entities;

public class SentryDeploymentConfig
{
    [Description("The amount of days to keep events for")]
    public int EventRetentionDays { get; set; } = 90;
    
    [Description("You can either use a port number or an IP:PORT combo for SENTRY_BIND")]
    public string Bind { get; set; } = "9000";
    
    [Description("The registry to use for images; defaults to docker.io")]
    public string? Registry { get; set; }
    
    [Description("The image to use for Sentry")]
    public string? Image { get; set; }
    
    [Description("The image to use for Snuba")]
    public string? SnubaImage { get; set; }
    
    [Description("The image to use for Relay")]
    public string? RelayImage { get; set; }
    
    [Description("The image to use for Uptime")]
    public string? UptimeImage { get; set; }
    
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
    
    [Description("The address to use for statsd")]
    public string StatsdAddress { get; set; } = "statsd:9125";

    public string ReplaceVariables(string yaml, string version)
    {
        var replacements = new Dictionary<string, string>
        {
            ["SENTRY_EVENT_RETENTION_DAYS"] = EventRetentionDays.ToString(),
            ["SENTRY_BIND"] = Bind,
            ["SENTRY_IMAGE"] = Image ?? $"{Registry + (Registry == null || Registry.EndsWith('/') ? "" : "/")}getsentry/sentry:{version}",
            ["SNUBA_IMAGE"] = SnubaImage ?? $"{Registry + (Registry == null || Registry.EndsWith('/') ? "" : "/")}getsentry/snuba:{version}",
            ["RELAY_IMAGE"] = RelayImage ?? $"{Registry + (Registry == null || Registry.EndsWith('/') ? "" : "/")}getsentry/relay:{version}",
            ["UPTIME_CHECKER_IMAGE"] = UptimeImage ?? $"{Registry + (Registry == null || Registry.EndsWith('/') ? "" : "/")}getsentry/uptime-checker:{version}",
            ["TASKBROKER_IMAGE"] = TaskbrokerImage ?? $"{Registry + (Registry == null || Registry.EndsWith('/') ? "" : "/")}getsentry/taskbroker:{version}",
            ["SYMBOLICATOR_IMAGE"] = SymbolicatorImage ?? $"{Registry + (Registry == null || Registry.EndsWith('/') ? "" : "/")}getsentry/symbolicator:{version}",
            ["VROOM_IMAGE"] = VroomImage ?? $"{Registry + (Registry == null || Registry.EndsWith('/') ? "" : "/")}getsentry/vroom:{version}",
            ["WAL2JSON_VERSION"] = Wal2JsonVersion,
            ["HEALTHCHECK_START_PERIOD"] = HealthCheckStartPeriod,
            ["HEALTHCHECK_INTERVAL"] = HealthCheckInterval,
            ["HEALTHCHECK_TIMEOUT"] = HealthCheckTimeout,
            ["HEALTHCHECK_RETRIES"] = HealthCheckRetries,
            ["HEALTHCHECK_FILE_START_PERIOD"] = HealthCheckFileStartPeriod,
            ["HEALTHCHECK_FILE_INTERVAL"] = HealthCheckFileInterval,
            ["HEALTHCHECK_FILE_TIMEOUT"] = HealthCheckFileTimeout,
            ["HEALTHCHECK_FILE_RETRIES"] = HealthCheckFileRetries,
            ["STATSD_ADDR"] = StatsdAddress,
            ["SENTRY_STATSD_ADDR"] = StatsdAddress,
            ["SYMBOLICATOR_STATSD_ADDR"] = StatsdAddress,
            ["TASKBROKER_STATSD_ADDR"] = StatsdAddress,
            ["SNUBA_STATSD_ADDR"] = StatsdAddress,
        };
        var result = new StringBuilder(yaml);
        
        foreach (var (key, value) in replacements)
        {
            result.Replace($"${key}", value);
        }

        return Regex.Replace(result.ToString(), "\\${(.+?)(?::-(.*?))?}", match => replacements.TryGetValue(match.Groups[1].Value, out var v) ? v : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[1].Value);
    }
}