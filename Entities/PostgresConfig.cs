using System.Text.Json.Serialization;

namespace SentryOperator.Entities;

public class PostgresConfig
{
    public string? Engine { get; set; } = "sentry.db.postgres";
    
    public string? Name { get; set; } = "postgres";
    
    public string? User { get; set; } = "postgres";
    
    public string? Password { get; set; } = "";
    
    public string? Host { get; set; } = "postgres";
    
    public string? Port { get; set; } = "5432";
}