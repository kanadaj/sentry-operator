using SentryOperator.Docker.Volume;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public class SentryWebConverter : SentryContainerConverter
{
    public override int Priority => 1;
    public override bool CanConvert(string name, DockerService service) => name == "web";
    
    protected override IEnumerable<VolumeRef> GetVolumeData(DockerService service, SentryDeployment sentryDeployment)
    {
        var knownNames = new List<string>();
        foreach(var volume in base.GetVolumeData(service, sentryDeployment))
        {
            if(knownNames.Contains(volume.Name)) continue;
            yield return volume;
            knownNames.Add(volume.Name);
        }
    }
}