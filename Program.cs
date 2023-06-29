using k8s;
using k8s.Models;
using KubeOps.Operator;
using SentryOperator.Docker;
using SentryOperator.Entities;

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



try
{
    var dockerComposeUrl = $"https://raw.githubusercontent.com/getsentry/self-hosted/master/docker-compose.yml";
    var dockerComposeRaw = await new HttpClient().GetStringAsync(dockerComposeUrl);
    dockerComposeRaw = new SentryDeploymentConfig().ReplaceVariables(dockerComposeRaw);
    var dockerComposeConverter = new DockerComposeConverter();
    var (deployments, services) = dockerComposeConverter.Convert(dockerComposeRaw, new SentryDeployment()
    {
        Metadata = new V1ObjectMeta()
        {
            Name = "sentry",
            NamespaceProperty = "sentry",
        },  
    });
    using var file = File.Create("kubernetes-deploy.yml");
    using var writer = new StreamWriter(file);
    foreach (var deployment in deployments)
    {
        writer.WriteLine(KubernetesYaml.Serialize(deployment));
        writer.WriteLine("---");
    }
    foreach (var service in services)
    {
        writer.WriteLine(KubernetesYaml.Serialize(service));
        writer.WriteLine("---");
    }
    var certificate = new Certificate()
    {
        ApiVersion = "cert-manager.io/v1",
        Kind = "Certificate",
        Metadata = new V1ObjectMeta
        {
            Name = "sentry-cert",
            NamespaceProperty = "sentry"
        },
        Spec = new Certificate.CertificateSpec()
        {
            CommonName = "sentry." + "sentry" + ".svc.cluster.local",
            Duration = "87600h",
            DnsNames = new List<string>()
            {
                "sentry",
                "sentry" + "." + "sentry",
                "sentry" + "." + "sentry" + ".svc.cluster.local"
            },
            IssuerRef = new Certificate.IssuerReference()
            {
                Name = "self-signed",
                Kind = "ClusterIssuer"
            },
            SecretName = "sentry-cert"
        }
    };
    writer.WriteLine(KubernetesYaml.Serialize(certificate));
}
catch(Exception e)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine(e.StackTrace);
}

await app.RunOperatorAsync(args);