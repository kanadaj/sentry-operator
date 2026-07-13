using Microsoft.Extensions.Logging;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace SentryOperator.Tests.TestSupport;

public class TestLoggerFactory :ILoggerFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public TestLoggerFactory(ITestOutputHelper output)
    {
        _loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog(new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.TestOutput(output)
            .CreateLogger()));
    }

    public void Dispose() => _loggerFactory.Dispose();

    public void AddProvider(ILoggerProvider provider) => _loggerFactory.AddProvider(provider);

    public ILogger CreateLogger(string categoryName) => _loggerFactory.CreateLogger(categoryName);
}