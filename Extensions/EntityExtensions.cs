using System.Security.Cryptography;
using System.Text;
using k8s;

namespace SentryOperator.Extensions;

public static class EntityExtensions
{
    public static string GetChecksum(this IKubernetesObject entity)
    {
        var checksumBytes = MD5.HashData(Encoding.UTF8.GetBytes(KubernetesYaml.Serialize(entity)));
        var checksum = BitConverter.ToString(checksumBytes).Replace("-", "");
        return checksum;
    }
}