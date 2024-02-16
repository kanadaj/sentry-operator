using System.Security.Cryptography;
using System.Text;
using k8s;
using k8s.Models;
using SentryOperator.Entities;

namespace SentryOperator.Extensions;

public static class EntityExtensions
{
    public static string GetChecksum(this IKubernetesObject entity)
    {
        var checksumBytes = MD5.HashData(Encoding.UTF8.GetBytes(KubernetesYaml.Serialize(entity)));
        var checksum = BitConverter.ToString(checksumBytes).Replace("-", "");
        return checksum;
    }
    public static string GetChecksum(this IList<IKubernetesObject<V1ObjectMeta>> entities)
    {
        var checksumBytes = MD5.HashData(Encoding.UTF8.GetBytes(KubernetesYaml.Serialize(entities)));
        var checksum = BitConverter.ToString(checksumBytes).Replace("-", "");
        return checksum;
    }
    public static string GetChecksum(this SentryDeployment.SentryDeploymentSpec entity)
    {
        var checksumBytes = MD5.HashData(Encoding.UTF8.GetBytes(KubernetesYaml.Serialize(entity)));
        var checksum = BitConverter.ToString(checksumBytes).Replace("-", "");
        return checksum;
    }
}