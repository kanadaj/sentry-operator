using System.Text.RegularExpressions;
using KubeOps.Operator.Webhooks;
using SentryOperator.Entities;

namespace SentryOperator.Webhooks;

public class SentryDeploymentValidator : IValidationWebhook<SentryDeployment>
{
    public AdmissionOperations Operations => AdmissionOperations.Create;

    public ValidationResult Create(SentryDeployment newEntity, bool dryRun)
    {
        if (newEntity.Spec.Version == null && newEntity.Spec.DockerComposeUrl == null)
        {
            return ValidationResult.Fail(StatusCodes.Status400BadRequest, "Either version or docker compose url must be set.");
        }

        if (newEntity.Spec.Version != null)
        {
            // Sentry versions are in the format of 21.5.0, 21.5.1, 21.5.2, etc.
            if (!Regex.IsMatch(newEntity.Spec.Version, @"^\d+\.\d+\.\d+$"))
            {
                return ValidationResult.Fail(StatusCodes.Status400BadRequest, "Version must be in the format of 21.5.0, 21.5.1, 21.5.2, etc.");
            }
        }

        if (newEntity.Spec.DockerComposeUrl != null)
        {
            if(!Uri.TryCreate(newEntity.Spec.DockerComposeUrl, UriKind.Absolute, out var _))
            {
                return ValidationResult.Fail(StatusCodes.Status400BadRequest, "Docker compose url must be a valid url.");
            }
        }
        
        return ValidationResult.Success();
    }
}
