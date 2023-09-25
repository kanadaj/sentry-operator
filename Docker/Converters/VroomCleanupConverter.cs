using k8s.Models;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public class VroomCleanupConverter : CleanupConverter
{
    public override bool CanConvert(string name, DockerService service) => service.Image == "vroom-cleanup-self-hosted-local";

    protected override string GetImage(string version) => $"getsentry/vroom:{version}";

    protected override string CronSchedule => "0 0 * * *";
    protected override string CronTask => "find /var/lib/sentry-profiles -type f -mtime +$SENTRY_EVENT_RETENTION_DAYS -delete";
}