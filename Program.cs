using KubeOps.Operator;
using SentryOperator.Docker;
using SentryOperator.Docker.Converters;
using SentryOperator.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddKubernetesOperator();
builder.Services.AddMemoryCache();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddSerilog(new LoggerConfiguration()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger());
});
builder.Services.AddTransient<DockerComposeConverter>();
builder.Services.AddHttpClient<RemoteFileService>();

// Find all non-abstract IDockerContainerConverter implementations and register them.
foreach (var converter in typeof(Program).Assembly.GetTypes()
    .Where(t => !t.IsAbstract && typeof(IDockerContainerConverter).IsAssignableFrom(t)))
{
    builder.Services.AddTransient(typeof(IDockerContainerConverter), converter);
}

builder.Services.AddKubernetesOperator()
    .RegisterComponents();

var app = builder.Build();

await app.RunAsync();