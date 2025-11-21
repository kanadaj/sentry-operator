using SentryOperator.Docker.Volume;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public class SymbolicatorCleanupConverter : CleanupConverter
{
    public override bool CanConvert(string name, DockerService service) => service.Image == "symbolicator-cleanup-self-hosted-local" || name == "symbolicator-cleanup";
    protected override string GetImage(string version)
    {
        return $"getsentry/symbolicator:nightly";
    }

    protected override IEnumerable<VolumeRef> GetVolumeData(DockerService service, SentryDeployment sentryDeployment)
    {
        foreach(var volume in base.GetVolumeData(service, sentryDeployment))
        {
            yield return volume;
        }
        
        yield return new ConfigMapVolumeRef("symbolicator-conf", "/etc/symbolicator/config.yml", "config.yml");
    }

    protected override string CronSchedule => "55 23 * * *";
    protected override string CronTask => "gosu symbolicator symbolicator cleanup";
}