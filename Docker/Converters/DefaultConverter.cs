namespace SentryOperator.Docker.Converters;

public class DefaultConverter : ContainerConverter
{
    public override int Priority => -1;
    public override bool CanConvert(string name, DockerService service) => true;
}