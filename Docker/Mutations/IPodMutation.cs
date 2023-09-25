using k8s.Models;

namespace SentryOperator.Docker.Mutations;

public interface IPodMutation
{
    bool ShouldApplyMutation(V1Pod pod);
    
    void ApplyMutation(V1Pod pod);
}