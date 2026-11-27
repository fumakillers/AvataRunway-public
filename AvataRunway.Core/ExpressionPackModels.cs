namespace AvataRunway.Core;

public sealed class ExpressionPackManifest
{
    public string Format { get; set; } = ExpressionPackService.FormatIdentifier;
    public int FormatVersion { get; set; } = 1;
    public ExpressionPackSource Source { get; set; } = new();
    public List<string> RequiredFeatures { get; set; } = ["morph-target-binds"];
    public ExpressionPackBinary Binary { get; set; } = new();
    public List<ExpressionPackMeshGroup> MeshGroups { get; set; } = [];
    public List<ExpressionPackMorphTarget> MorphTargets { get; set; } = [];
    public List<ExpressionPackExpression> Expressions { get; set; } = [];
    public Dictionary<string, object?> Extensions { get; set; } = [];
}

public sealed class ExpressionPackSource
{
    public string VrmGeneration { get; set; } = "1.0";
    public string SpecVersion { get; set; } = "1.0";
}

public sealed class ExpressionPackBinary
{
    public string Path { get; set; } = ExpressionPackService.BinaryEntryName;
    public string Format { get; set; } = "avatarunway-morph-buffer";
    public int FormatVersion { get; set; } = 1;
    public long ByteLength { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class ExpressionPackMeshGroup
{
    public string Id { get; set; } = string.Empty;
    public int SourceMeshIndex { get; set; }
    public List<int> SourceNodeIndices { get; set; } = [];
    public List<ExpressionPackPrimitive> Primitives { get; set; } = [];
}

public sealed class ExpressionPackPrimitive
{
    public int PrimitiveIndex { get; set; }
    public PrimitiveFingerprint Fingerprint { get; set; } = null!;
}

public sealed class ExpressionPackMorphTarget
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string MeshGroupId { get; set; } = string.Empty;
    public int SourceTargetIndex { get; set; }
    public List<ExpressionPackMorphPrimitive> Primitives { get; set; } = [];
}

public sealed class ExpressionPackMorphPrimitive
{
    public int PrimitiveIndex { get; set; }
    public Dictionary<string, ExpressionPackAttribute> Attributes { get; set; } = [];
}

public sealed class ExpressionPackAttribute
{
    public long ByteOffset { get; set; }
    public long ByteLength { get; set; }
    public string ComponentType { get; set; } = "FLOAT32";
    public string Type { get; set; } = "VEC3";
    public int Count { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public float[]? Min { get; set; }
    public float[]? Max { get; set; }
}

public sealed class ExpressionPackExpression
{
    public string Name { get; set; } = string.Empty;
    public string SourceKind { get; set; } = "vrm1-custom";
    public bool IsBinary { get; set; }
    public string OverrideBlink { get; set; } = "none";
    public string OverrideLookAt { get; set; } = "none";
    public string OverrideMouth { get; set; } = "none";
    public List<ExpressionPackMorphBind> MorphTargetBinds { get; set; } = [];
}

public sealed class ExpressionPackMorphBind
{
    public string MorphTargetId { get; set; } = string.Empty;
    public string MeshGroupId { get; set; } = string.Empty;
    public int SourceNodeIndex { get; set; }
    public float Weight { get; set; }
}

public sealed record LoadedExpressionPack(ExpressionPackManifest Manifest, byte[] BinaryData);
