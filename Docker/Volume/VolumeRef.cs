using k8s.Models;

namespace SentryOperator.Docker.Volume;

public record VolumeRef(string Name, string Path, string SubPath = "");
public record ConfigMapVolumeRef(string Name, string Path, string SubPath = "", string? ConfigMapName = null, V1KeyToPath[]? Items = null) : VolumeRef(Name, Path, SubPath);

public record SecretVolumeRef(string Name, string Path, string SecretName, string SubPath = "") : VolumeRef(Name, Path, SubPath);