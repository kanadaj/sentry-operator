using System.Text;
using k8s.Models;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public class TaskWorkerConverter : SentryContainerConverter
{
    public override bool CanConvert(string name, DockerService service) => name == "taskworker";

    protected override V1Container GetBaseContainer(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        var container = base.GetBaseContainer(name, service, sentryDeployment);
        
        container.Args ??= new List<string>();
        if(sentryDeployment.Spec.Config?.TaskWorkerConcurrency is > 4)
        {
            var arg = container.Args.FirstOrDefault(x => x.Contains("--concurrency"));
            if (arg != null)
            {
                container.Args.Remove(arg);
            }
            container.Args.Add($"--concurrency={sentryDeployment.Spec.Config.TaskWorkerConcurrency}");
        }

        if ((sentryDeployment.Spec.Replicas?.TryGetValue("taskbroker", out var brokerCount) ?? false) && brokerCount > 1)
        {
            // We remove the rpc-host option and instead build a rpc-host-list from 0 to N-1 using taskbroker-N.taskbroker:50051
            var arg = container.Args.FirstOrDefault(x => x.Contains("--rpc-host"));
            if (arg != null)
            {
                container.Args.Remove(arg);
            }

            var option = new StringBuilder("--rpc-host-list=");
            for (int i = 0; i < brokerCount; i++)
            {
                option.Append("taskbroker-").Append(i).Append(".taskbroker:50051").Append(",");
            }
            option.Remove(option.Length - 1, 1);
            container.Args.Add(option.ToString());
        }

        return container;
    }
}