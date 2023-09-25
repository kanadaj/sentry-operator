namespace SentryOperator.Docker.Converters;

public class SentryWebConverter : SentryContainerConverter
{
    public override int Priority => 1;
    public override bool CanConvert(string name, DockerService service) => name == "web";
}