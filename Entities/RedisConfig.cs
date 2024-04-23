namespace SentryOperator.Entities;

public class RedisConfig
{
    public string? Host { get; set; } = "redis";

    public string? Password { get; set; } = "";
    
    public string? Port { get; set; } = "6379";
    
    public string? Database { get; set; } = "0";
}