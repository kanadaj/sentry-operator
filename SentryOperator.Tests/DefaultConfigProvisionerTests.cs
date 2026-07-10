using SentryOperator.Entities;
using SentryOperator.Services;

namespace SentryOperator.Tests;

public class DefaultConfigProvisionerTests
{
    [Fact]
    public void GenerateRedisConfig_UsesConfiguredHostsAndDefaults()
    {
        var config = DefaultConfigProvisioner.GenerateRedisConfig(
        [
            new RedisConfig
            {
                Host = "redis-primary",
                Port = "6380",
                Password = "secret",
                Database = "2",
            },
            new RedisConfig(),
        ]);

        Assert.Contains("\"host\": \"redis-primary\"", config);
        Assert.Contains("\"port\": \"6380\"", config);
        Assert.Contains("\"password\": \"secret\"", config);
        Assert.Contains("\"db\": \"2\"", config);
        Assert.Contains("\"host\": \"redis\"", config);
        Assert.Contains("0: {", config);
        Assert.Contains("1: {", config);
    }
}
