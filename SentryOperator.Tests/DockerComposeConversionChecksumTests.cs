using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using SentryOperator.Docker;
using SentryOperator.Docker.Converters;
using SentryOperator.Entities;
using SentryOperator.Extensions;

namespace SentryOperator.Tests;

public class DockerComposeConversionChecksumTests
{
    [Fact]
    public void CurrentComposeSnapshot_ProducesStableGeneratedResourceChecksums()
    {
        var composeYaml = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "TestData", "docker-compose.yml"));
        var deployment = CreateDeployment();
        var converter = new DockerComposeConverter(
            NullLogger<DockerComposeConverter>.Instance,
            CreateConverters());

        var resources = converter.Convert(
            deployment.Spec.Config!.ReplaceVariables(composeYaml, deployment.Spec.GetVersion()),
            deployment);
        Assert.NotEmpty(resources);
        Assert.All(resources, resource => Assert.False(string.IsNullOrWhiteSpace(resource.GetChecksum())));
        Assert.Equal(ExpectedCombinedChecksum, resources.GetChecksum());
    }

    private static IEnumerable<IDockerContainerConverter> CreateConverters()
    {
        return typeof(IDockerContainerConverter).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract &&
                           typeof(IDockerContainerConverter).IsAssignableFrom(type))
            .Select(type => (IDockerContainerConverter)Activator.CreateInstance(type)!);
    }

    private static SentryDeployment CreateDeployment()
    {
        return new SentryDeployment
        {
            Metadata = new V1ObjectMeta
            {
                Name = "sentry",
                NamespaceProperty = "default",
            },
            Spec = new SentryDeployment.SentryDeploymentSpec
            {
                Version = "23.6.1",
                Config = new SentryDeploymentConfig(),
                Environment = new Dictionary<string, string>
                {
                    ["GEOIPUPDATE_LICENSE_KEY"] = "test-license",
                    ["GEOIPUPDATE_ACCOUNT_ID"] = "test-account",
                    ["GEOIPUPDATE_EDITION_IDS"] = "GeoLite2-City",
                },
            },
        };
    }

    private const string ExpectedCombinedChecksum = "AD35C6B80DAB1C46A21AA3D1D90CD645";
}
