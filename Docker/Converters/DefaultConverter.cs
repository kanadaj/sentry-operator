﻿using k8s.Models;
using SentryOperator.Entities;

namespace SentryOperator.Docker.Converters;

public class DefaultConverter : ContainerConverter
{
    public override int Priority => -1;
    public override bool CanConvert(string name, DockerService service) => true;

    protected override V1Container GetBaseContainer(string name, DockerService service, SentryDeployment sentryDeployment)
    {
        var container = base.GetBaseContainer(name, service, sentryDeployment);
        
        
        container.EnvFrom ??= new List<V1EnvFromSource>();
        container.EnvFrom.Add(new V1EnvFromSource
        {
            SecretRef = new V1SecretEnvSource("sentry-env")
        });

        return container;
    }
}