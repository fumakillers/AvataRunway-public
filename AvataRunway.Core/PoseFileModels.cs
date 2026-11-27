namespace AvataRunway.Core;

public sealed class PoseFile
{
    public string Format { get; set; } = PoseFileService.FormatIdentifier;
    public int Version { get; set; } = PoseFileService.CurrentVersion;
    public string RotationSpace { get; set; } = PoseFileService.RotationSpaceIdentifier;
    public string EulerOrder { get; set; } = PoseFileService.EulerOrderIdentifier;
    public string TranslationSpace { get; set; } = PoseFileService.TranslationSpaceIdentifier;
    public PoseVector3 HipsTranslation { get; set; } = new();
    public Dictionary<string, PoseBone> Bones { get; set; } = new(StringComparer.Ordinal);
}

public sealed class PoseBone
{
    public PoseQuaternion Rotation { get; set; } = new();
}

public sealed class PoseQuaternion
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double W { get; set; } = 1.0;
}

public sealed class PoseVector3
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}
