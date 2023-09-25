using k8s.Models;
using SentryOperator.Docker.Volume;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public class GeoIPConverter : ContainerConverter
{
    public override bool CanConvert(string name, DockerService service) => name == "geoipupdate";

    protected override V1Container GetBaseContainer(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        var container = base.GetBaseContainer(name, service, sentryDeployment);

        container.Env ??= new List<V1EnvVar>();
        container.Env.Add(new V1EnvVar("GEOIPUPDATE_LICENSE_KEY", sentryDeployment.Spec.Environment["GEOIPUPDATE_LICENSE_KEY"]));
        container.Env.Add(new V1EnvVar("GEOIPUPDATE_ACCOUNT_ID", sentryDeployment.Spec.Environment["GEOIPUPDATE_ACCOUNT_ID"]));
        container.Env.Add(new V1EnvVar("GEOIPUPDATE_EDITION_IDS", sentryDeployment.Spec.Environment["GEOIPUPDATE_EDITION_IDS"]));
        
        container.Command = new[] { "/bin/sh", "-ce" };
        
        container.Args = new[]
        {
            "touch crontab.tmp && echo '0 0 * * * /usr/bin/geoipupdate -d /sentry -f /sentry/GeoIP.conf' > crontab.tmp && crontab crontab.tmp && rm -rf crontab.tmp && /usr/sbin/crond -f -d 0"
        };

        return container;
    }

    protected override IEnumerable<VolumeRef> GetVolumeData(DockerService service, SentryDeployment sentryDeployment)
    {
        foreach(var volume in base.GetVolumeData(service, sentryDeployment))
        {
            yield return volume;
        }
        
        yield return new ConfigMapVolumeRef("geoip-conf", "/sentry/GeoIP.conf", "GeoIP.conf");
    }
}