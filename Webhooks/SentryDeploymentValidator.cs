using System.Text.RegularExpressions;
using KubeOps.Operator.Web.Webhooks.Admission.Validation;
using SentryOperator.Entities;

namespace SentryOperator.Webhooks;

[ValidationWebhook(typeof(SentryDeployment))]
public class SentryDeploymentValidator : ValidationWebhook<SentryDeployment>
{
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
        
        return Success();
    }
}
