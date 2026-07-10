using System.Text;
using k8s.Models;
using SentryOperator.Docker.Compose;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public class TaskWorkerConverter : SentryContainerConverter
{
    public override int Priority => 1;
    public override bool CanConvert(string name, DockerService service) => name == "taskworker";

    protected override V1Container GetBaseContainer(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        var container = base.GetBaseContainer(name, service, sentryDeployment);

        var args = container.Args[0].Replace("$SENTRY_TASKWORKER_CONCURRENCY", "4").Split(" ").ToList();
        
        if(sentryDeployment.Spec.Config?.TaskWorkerConcurrency is not null and not 4)
        {
            var arg = args.FirstOrDefault(x => x.Contains("--concurrency"));
            if (arg != null)
            {
                args.Remove(arg);
            }
            args.Add($"--concurrency={sentryDeployment.Spec.Config.TaskWorkerConcurrency}");
        }

        if ((sentryDeployment.Spec.Replicas?.TryGetValue("taskbroker", out var brokerCount) ?? false) && brokerCount > 1)
        {
            // We remove the rpc-host option and instead build a rpc-host-list from 0 to N-1 using taskbroker-N.taskbroker:50051
            var arg = args.FirstOrDefault(x => x.Contains("--rpc-host"));
            if (arg != null)
            {
                args.Remove(arg);
            }

            var option = new StringBuilder("--rpc-host-list=");
            for (int i = 0; i < brokerCount; i++)
            {
                option.Append("taskbroker-").Append(i).Append(".taskbroker:50051").Append(",");
            }
            option.Remove(option.Length - 1, 1);
            args.Add(option.ToString());
        }
        container.Args[0] = string.Join(" ", args);

        return container;
    }
}