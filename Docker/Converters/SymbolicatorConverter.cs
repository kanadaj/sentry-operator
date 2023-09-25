using SentryOperator.Docker.Volume;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public class SymbolicatorConverter : ContainerConverter
{
    public override bool CanConvert(string name, DockerService service) => name == "symbolicator";

    protected override IEnumerable<VolumeRef> GetVolumeData(DockerService service, SentryDeployment sentryDeployment)
    {
        foreach(var volume in base.GetVolumeData(service, sentryDeployment))
        {
            yield return volume;
        }
        
        yield return new ConfigMapVolumeRef("symbolicator-conf", "/etc/symbolicator/config.yml", "config.yml");
    }
}