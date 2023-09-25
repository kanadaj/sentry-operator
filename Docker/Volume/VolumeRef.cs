namespace SentryOperator.Docker.Volume;

public record VolumeRef(string Name, string Path, string SubPath = "");
public record ConfigMapVolumeRef(string Name, string Path, string SubPath = "", string? ConfigMapName = null) : VolumeRef(Name, Path, SubPath);

public record SecretVolumeRef(string Name, string Path, string SecretName, string SubPath = "") : VolumeRef(Name, Path, SubPath);