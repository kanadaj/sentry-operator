using KubeOps.Operator;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddKubernetesOperator();
builder.Services.AddMemoryCache();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddLogging(logging =>
{
    logging.AddConsole();
});

var app = builder.Build();
app.UseKubernetesOperator();

await app.RunOperatorAsync(args);