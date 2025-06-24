using System.Text.RegularExpressions;
using KubeOps.Operator.Web.Webhooks.Admission.Validation;
using SentryOperator.Controller;
using SentryOperator.Docker;
using SentryOperator.Entities;
using SentryOperator.Services;
using YamlDotNet.Core;

namespace SentryOperator.Webhooks;

[ValidationWebhook(typeof(SentryDeployment))]
public class SentryDeploymentValidator : ValidationWebhook<SentryDeployment>
{
    private readonly RemoteFileService _remoteFileService;
    private readonly DockerComposeConverter _dockerComposeConverter;

    public SentryDeploymentValidator(RemoteFileService remoteFileService, DockerComposeConverter dockerComposeConverter)
    {
        _remoteFileService = remoteFileService;
        _dockerComposeConverter = dockerComposeConverter;
    }

    public override ValidationResult Create(SentryDeployment newEntity, bool dryRun)
    {
        if (newEntity.Spec.Version == null && newEntity.Spec.DockerComposeUrl == null)
        {
            return Fail("Either version or docker compose url must be set.", StatusCodes.Status400BadRequest);
        }

        if (newEntity.Spec.Version != null)
        {
            // Sentry versions are in the format of 21.5.0, 21.5.1, 21.5.2, etc.
            if (!Regex.IsMatch(newEntity.Spec.Version, @"^(nightly)|(\d+\.\d+\.\d+)$"))
            {
                return Fail("Version must be in the format of 21.5.0, 21.5.1, 21.5.2, etc.", StatusCodes.Status400BadRequest);
            }
        }

        if (newEntity.Spec.DockerComposeUrl != null)
        {
            if(!Uri.TryCreate(newEntity.Spec.DockerComposeUrl, UriKind.Absolute, out var _))
            {
                return Fail("Docker compose url must be a valid url.", StatusCodes.Status400BadRequest);
            }
        }

        if (!string.IsNullOrWhiteSpace(newEntity.Spec.DockerComposeOverrides))
        {
            // Validate that the docker compose overrides are valid YAML
            try
            {
                
                var dockerComposeUrl = SentryDeploymentController.DockerComposeUrl;
                if (newEntity.Spec.DockerComposeUrl != null)
                {
                    dockerComposeUrl = newEntity.Spec.DockerComposeUrl;
                }
                else if (newEntity.Spec.Version != null)
                {
                    dockerComposeUrl = $"https://raw.githubusercontent.com/getsentry/self-hosted/{(newEntity.Spec.Version == "nightly" ? "master" : newEntity.Spec.Version)}/docker-compose.yml";
                }

                var dockerComposeRaw = _remoteFileService.Get(dockerComposeUrl);

                _dockerComposeConverter.Parse(dockerComposeRaw, newEntity.Spec.DockerComposeOverrides);
            }
            catch (Exception ex)
            {
                return Fail($"Docker compose overrides are not valid YAML: {ex.Message}", StatusCodes.Status400BadRequest);
            }
        }
        
        return Success();
    }
}
