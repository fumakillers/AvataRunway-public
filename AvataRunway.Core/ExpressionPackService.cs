using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AvataRunway.Core;

public sealed class ExpressionPackService
{
    public const string FormatIdentifier = "avatarunway-expression-pack";
    public const string ManifestEntryName = "manifest.json";
    public const string BinaryEntryName = "morphs.bin";

    private static readonly byte[] BinaryMagic = "AVEXPBIN"u8.ToArray();
    private static readonly string[] MorphSemantics = ["POSITION", "NORMAL", "TANGENT"];
    private static readonly HashSet<string> Vrm1ReservedPresetNames = new(StringComparer.Ordinal)
    {
        "happy", "angry", "sad", "relaxed", "surprised",
        "aa", "ih", "ou", "ee", "oh",
        "blink", "blinkLeft", "blinkRight",
        "lookUp", "lookDown", "lookLeft", "lookRight", "neutral"
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly MeshFingerprintService fingerprintService = new();

    public void ExportVrm1Custom(GlbDocument source, IEnumerable<string> expressionNames, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(expressionNames);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The destination stream is not writable.", nameof(destination));
        }

        var vrm = GetVrm1Extension(source.Root);
        var custom = vrm["expressions"]?["custom"]?.AsObject()
            ?? throw new InvalidVrmDataException("VRM 1.0 custom expressions are missing.");
        var selectedNames = expressionNames.Distinct(StringComparer.Ordinal).ToArray();
        if (selectedNames.Length == 0)
        {
            throw new InvalidVrmDataException("At least one custom expression must be selected.");
        }

        var manifest = new ExpressionPackManifest();
        using var binary = new MemoryStream();
        binary.Write(BinaryMagic);
        WriteUInt32(binary, 1);
        WriteUInt32(binary, 0);

        var meshGroupsByMesh = new Dictionary<int, ExpressionPackMeshGroup>();
        var morphsBySourceSlot = new Dictionary<(int Mesh, int Target), ExpressionPackMorphTarget>();
        foreach (var name in selectedNames)
        {
            if (Vrm1ReservedPresetNames.Contains(name))
            {
                throw new InvalidVrmDataException($"The custom expression name '{name}' is reserved by VRM 1.0.");
            }
            if (custom[name] is not JsonObject expression)
            {
                throw new InvalidVrmDataException($"The custom expression '{name}' does not exist.");
            }
            RejectUnsupportedBinds(name, expression);
            var binds = expression["morphTargetBinds"]?.AsArray();
            if (binds is null || binds.Count == 0)
            {
                throw new InvalidVrmDataException($"The custom expression '{name}' has no Morph Target binds.");
            }

            var packExpression = new ExpressionPackExpression
            {
                Name = name,
                IsBinary = expression["isBinary"]?.GetValue<bool>() ?? false,
                OverrideBlink = ReadOverride(expression, "overrideBlink"),
                OverrideLookAt = ReadOverride(expression, "overrideLookAt"),
                OverrideMouth = ReadOverride(expression, "overrideMouth")
            };

            foreach (var bindNode in binds)
            {
                var bind = bindNode?.AsObject()
                    ?? throw new InvalidVrmDataException("A Morph Target bind is invalid.");
                var nodeIndex = bind["node"]?.GetValue<int>()
                    ?? throw new InvalidVrmDataException("A VRM 1.0 Morph Target bind node is missing.");
                var targetIndex = bind["index"]?.GetValue<int>()
                    ?? throw new InvalidVrmDataException("A VRM 1.0 Morph Target bind index is missing.");
                var weight = bind["weight"]?.GetValue<float>() ?? 1f;
                if (!float.IsFinite(weight) || weight < 0f || weight > 1f)
                {
                    throw new InvalidVrmDataException("A VRM 1.0 Morph Target bind weight is outside 0.0 through 1.0.");
                }

                var node = GltfAccessorReader.GetObject(source.Root, "nodes", nodeIndex);
                var meshIndex = node["mesh"]?.GetValue<int>()
                    ?? throw new InvalidVrmDataException("A VRM 1.0 Morph Target bind node has no mesh.");
                var meshGroup = GetOrCreateMeshGroup(source, meshIndex, nodeIndex, meshGroupsByMesh, manifest);
                if (!morphsBySourceSlot.TryGetValue((meshIndex, targetIndex), out var morphTarget))
                {
                    morphTarget = ExportMorphTarget(source, meshGroup, targetIndex, binary);
                    morphsBySourceSlot.Add((meshIndex, targetIndex), morphTarget);
                    manifest.MorphTargets.Add(morphTarget);
                }
                else if (!meshGroup.SourceNodeIndices.Contains(nodeIndex))
                {
                    meshGroup.SourceNodeIndices.Add(nodeIndex);
                }

                packExpression.MorphTargetBinds.Add(new ExpressionPackMorphBind
                {
                    MorphTargetId = morphTarget.Id,
                    MeshGroupId = meshGroup.Id,
                    SourceNodeIndex = nodeIndex,
                    Weight = weight
                });
            }

            manifest.Expressions.Add(packExpression);
        }

        var binaryBytes = binary.ToArray();
        manifest.Binary.ByteLength = binaryBytes.Length;
        manifest.Binary.Sha256 = Sha256(binaryBytes);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        WriteEntry(archive, ManifestEntryName, manifestBytes, CompressionLevel.Optimal);
        WriteEntry(archive, BinaryEntryName, binaryBytes, CompressionLevel.Optimal);
    }

    public void ExportVrm0Unknown(GlbDocument source, IEnumerable<string> expressionNames, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(expressionNames);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The destination stream is not writable.", nameof(destination));
        }

        var vrm = GetVrm0Extension(source.Root);
        var groups = vrm["blendShapeMaster"]?["blendShapeGroups"]?.AsArray()
            ?? throw new InvalidVrmDataException("VRM 0.x BlendShapeClips are missing.");
        var selectedNames = expressionNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (selectedNames.Length == 0)
        {
            throw new InvalidVrmDataException("At least one Unknown BlendShapeClip must be selected.");
        }

        var manifest = new ExpressionPackManifest
        {
            Source = new ExpressionPackSource
            {
                VrmGeneration = "0.x",
                SpecVersion = vrm["specVersion"]!.GetValue<string>()
            }
        };
        using var binary = new MemoryStream();
        binary.Write(BinaryMagic);
        WriteUInt32(binary, 1);
        WriteUInt32(binary, 0);
        var meshGroupsByMesh = new Dictionary<int, ExpressionPackMeshGroup>();
        var morphsBySourceSlot = new Dictionary<(int Mesh, int Target), ExpressionPackMorphTarget>();

        foreach (var selectedName in selectedNames)
        {
            var matches = groups
                .OfType<JsonObject>()
                .Where(group => string.Equals(group["name"]?.GetValue<string>(), selectedName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidVrmDataException($"The Unknown BlendShapeClip '{selectedName}' is missing or ambiguous.");
            }
            var group = matches[0];
            if (!string.Equals(group["presetName"]?.GetValue<string>(), "unknown", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidVrmDataException($"The BlendShapeClip '{selectedName}' is a preset and cannot be exported by the MVP.");
            }
            if (group["materialValues"] is JsonArray materialValues && materialValues.Count > 0)
            {
                throw new InvalidVrmDataException($"The Unknown BlendShapeClip '{selectedName}' contains unsupported materialValues.");
            }
            var binds = group["binds"]?.AsArray();
            if (binds is null || binds.Count == 0)
            {
                throw new InvalidVrmDataException($"The Unknown BlendShapeClip '{selectedName}' has no Morph Target binds.");
            }

            var packExpression = new ExpressionPackExpression
            {
                Name = group["name"]!.GetValue<string>(),
                SourceKind = "vrm0-unknown",
                IsBinary = group["isBinary"]?.GetValue<bool>() ?? false
            };
            foreach (var bindNode in binds)
            {
                var bind = bindNode?.AsObject()
                    ?? throw new InvalidVrmDataException("A VRM 0.x BlendShape bind is invalid.");
                var meshIndex = bind["mesh"]?.GetValue<int>()
                    ?? throw new InvalidVrmDataException("A VRM 0.x BlendShape bind mesh is missing.");
                var targetIndex = bind["index"]?.GetValue<int>()
                    ?? throw new InvalidVrmDataException("A VRM 0.x BlendShape bind index is missing.");
                var sourceWeight = bind["weight"]?.GetValue<float>() ?? 100f;
                if (!float.IsFinite(sourceWeight) || sourceWeight < 0f || sourceWeight > 100f)
                {
                    throw new InvalidVrmDataException("A VRM 0.x BlendShape bind weight is outside 0 through 100.");
                }
                var meshGroup = GetOrCreateMeshGroup(source, meshIndex, null, meshGroupsByMesh, manifest);
                if (!morphsBySourceSlot.TryGetValue((meshIndex, targetIndex), out var morphTarget))
                {
                    morphTarget = ExportMorphTarget(source, meshGroup, targetIndex, binary);
                    morphsBySourceSlot.Add((meshIndex, targetIndex), morphTarget);
                    manifest.MorphTargets.Add(morphTarget);
                }
                packExpression.MorphTargetBinds.Add(new ExpressionPackMorphBind
                {
                    MorphTargetId = morphTarget.Id,
                    MeshGroupId = meshGroup.Id,
                    SourceNodeIndex = -1,
                    Weight = sourceWeight / 100f
                });
            }
            manifest.Expressions.Add(packExpression);
        }

        var binaryBytes = binary.ToArray();
        manifest.Binary.ByteLength = binaryBytes.Length;
        manifest.Binary.Sha256 = Sha256(binaryBytes);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        WriteEntry(archive, ManifestEntryName, manifestBytes, CompressionLevel.Optimal);
        WriteEntry(archive, BinaryEntryName, binaryBytes, CompressionLevel.Optimal);
    }

    public LoadedExpressionPack Read(Stream source, ExpressionPackReadLimits? limits = null)
    {
        try
        {
            return ReadCore(source, limits);
        }
        catch (InvalidVrmDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or NotSupportedException or
            JsonException or ArgumentException or InvalidOperationException or OverflowException or
            NullReferenceException or KeyNotFoundException)
        {
            throw new InvalidVrmDataException("The Expression Pack ZIP container is invalid or corrupted.", exception);
        }
    }

    private LoadedExpressionPack ReadCore(Stream source, ExpressionPackReadLimits? limits)
    {
        ArgumentNullException.ThrowIfNull(source);
        limits ??= ExpressionPackReadLimits.Default;
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count == 0 || archive.Entries.Count > limits.MaximumEntryCount)
        {
            throw new InvalidVrmDataException("The Expression Pack entry count is invalid.");
        }

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long totalLength = 0;
        foreach (var entry in archive.Entries)
        {
            ValidateEntryName(entry.FullName);
            if (!entries.TryAdd(entry.FullName, entry))
            {
                throw new InvalidVrmDataException("The Expression Pack contains duplicate entry names.");
            }
            if (entry.FullName is not (ManifestEntryName or BinaryEntryName))
            {
                throw new InvalidVrmDataException($"Unknown Expression Pack entry: {entry.FullName}");
            }

            var individualLimit = entry.FullName == ManifestEntryName
                ? limits.MaximumManifestLength
                : limits.MaximumBinaryLength;
            if (entry.Length < 0 || entry.Length > individualLimit)
            {
                throw new InvalidVrmDataException("An Expression Pack entry exceeds its size limit.");
            }
            if (entry.Length > 0 && (entry.CompressedLength <= 0 || entry.Length / (double)entry.CompressedLength > limits.MaximumCompressionRatio))
            {
                throw new InvalidVrmDataException("An Expression Pack entry exceeds the compression ratio limit.");
            }
            totalLength = checked(totalLength + entry.Length);
            if (totalLength > limits.MaximumTotalLength)
            {
                throw new InvalidVrmDataException("The Expression Pack exceeds the total expanded size limit.");
            }
        }

        if (!entries.TryGetValue(ManifestEntryName, out var manifestEntry) ||
            !entries.TryGetValue(BinaryEntryName, out var binaryEntry))
        {
            throw new InvalidVrmDataException("The Expression Pack required entries are missing.");
        }

        var manifestBytes = ReadEntry(manifestEntry, limits.MaximumManifestLength);
        var binaryBytes = ReadEntry(binaryEntry, limits.MaximumBinaryLength);
        ExpressionPackManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ExpressionPackManifest>(manifestBytes, JsonOptions)
                ?? throw new InvalidVrmDataException("The Expression Pack manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidVrmDataException("The Expression Pack manifest JSON is invalid.", exception);
        }

        ValidateManifest(manifest, binaryBytes);
        return new LoadedExpressionPack(manifest, binaryBytes);
    }

    private ExpressionPackMeshGroup GetOrCreateMeshGroup(
        GlbDocument source,
        int meshIndex,
        int? nodeIndex,
        Dictionary<int, ExpressionPackMeshGroup> byMesh,
        ExpressionPackManifest manifest)
    {
        if (byMesh.TryGetValue(meshIndex, out var existing))
        {
            if (nodeIndex is not null && !existing.SourceNodeIndices.Contains(nodeIndex.Value))
            {
                existing.SourceNodeIndices.Add(nodeIndex.Value);
            }
            return existing;
        }

        var mesh = GltfAccessorReader.GetObject(source.Root, "meshes", meshIndex);
        var primitives = mesh["primitives"]?.AsArray()
            ?? throw new InvalidVrmDataException("A mesh primitives array is missing.");
        var group = new ExpressionPackMeshGroup
        {
            Id = $"mesh-{meshIndex}",
            SourceMeshIndex = meshIndex,
            SourceNodeIndices = nodeIndex is null ? [] : [nodeIndex.Value]
        };
        for (var primitiveIndex = 0; primitiveIndex < primitives.Count; primitiveIndex++)
        {
            group.Primitives.Add(new ExpressionPackPrimitive
            {
                PrimitiveIndex = primitiveIndex,
                Fingerprint = fingerprintService.Create(source, meshIndex, primitiveIndex)
            });
        }
        byMesh.Add(meshIndex, group);
        manifest.MeshGroups.Add(group);
        return group;
    }

    private static ExpressionPackMorphTarget ExportMorphTarget(
        GlbDocument source,
        ExpressionPackMeshGroup meshGroup,
        int targetIndex,
        MemoryStream binary)
    {
        var mesh = GltfAccessorReader.GetObject(source.Root, "meshes", meshGroup.SourceMeshIndex);
        var primitives = mesh["primitives"]!.AsArray();
        var reader = new GltfAccessorReader(source);
        var morph = new ExpressionPackMorphTarget
        {
            Id = $"morph-{meshGroup.SourceMeshIndex}-{targetIndex}",
            Name = GetMorphTargetName(mesh, targetIndex),
            MeshGroupId = meshGroup.Id,
            SourceTargetIndex = targetIndex
        };

        for (var primitiveIndex = 0; primitiveIndex < primitives.Count; primitiveIndex++)
        {
            var primitive = primitives[primitiveIndex]!.AsObject();
            var targets = primitive["targets"]?.AsArray()
                ?? throw new InvalidVrmDataException("A bound mesh primitive has no Morph Targets.");
            if (targetIndex < 0 || targetIndex >= targets.Count || targets[targetIndex] is not JsonObject target)
            {
                throw new InvalidVrmDataException("A Morph Target bind index is out of range for a primitive.");
            }
            var packPrimitive = new ExpressionPackMorphPrimitive { PrimitiveIndex = primitiveIndex };
            foreach (var semantic in MorphSemantics)
            {
                if (target[semantic] is not JsonValue accessorNode)
                {
                    continue;
                }
                var accessorIndex = accessorNode.GetValue<int>();
                var info = reader.GetInfo(accessorIndex);
                if (info.Type != "VEC3" || info.ComponentType != 5126 || info.Count != meshGroup.Primitives[primitiveIndex].Fingerprint.VertexCount)
                {
                    throw new InvalidVrmDataException("A Morph Target accessor is not a matching FLOAT VEC3 accessor.");
                }
                var values = reader.ReadFloat32(accessorIndex);
                var bytes = FloatsToBytes(values);
                Align4(binary);
                var offset = binary.Position;
                binary.Write(bytes);
                var attribute = new ExpressionPackAttribute
                {
                    ByteOffset = offset,
                    ByteLength = bytes.Length,
                    Count = info.Count,
                    Sha256 = Sha256(bytes)
                };
                if (semantic == "POSITION")
                {
                    (attribute.Min, attribute.Max) = CalculateMinMax(values);
                }
                packPrimitive.Attributes.Add(semantic, attribute);
            }
            if (packPrimitive.Attributes.Count == 0)
            {
                throw new InvalidVrmDataException("A Morph Target contains no supported POSITION, NORMAL, or TANGENT data.");
            }
            morph.Primitives.Add(packPrimitive);
        }
        return morph;
    }

    private static JsonObject GetVrm1Extension(JsonObject root)
    {
        var vrm = root["extensions"]?["VRMC_vrm"]?.AsObject()
            ?? throw new InvalidVrmDataException("The VRMC_vrm extension is missing.");
        if (!string.Equals(vrm["specVersion"]?.GetValue<string>(), "1.0", StringComparison.Ordinal))
        {
            throw new InvalidVrmDataException("Only VRM 1.0 is supported by this operation.");
        }
        return vrm;
    }

    private static JsonObject GetVrm0Extension(JsonObject root)
    {
        var vrm = root["extensions"]?["VRM"]?.AsObject()
            ?? throw new InvalidVrmDataException("The VRM 0.x extension is missing.");
        var specVersion = vrm["specVersion"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(specVersion) || !specVersion.StartsWith("0.", StringComparison.Ordinal))
        {
            throw new InvalidVrmDataException("Only VRM 0.x is supported by this operation.");
        }
        return vrm;
    }

    private static void RejectUnsupportedBinds(string name, JsonObject expression)
    {
        foreach (var property in new[] { "materialColorBinds", "textureTransformBinds" })
        {
            if (expression[property] is JsonArray array && array.Count > 0)
            {
                throw new InvalidVrmDataException($"The custom expression '{name}' contains unsupported {property}.");
            }
        }
    }

    private static string ReadOverride(JsonObject expression, string name)
    {
        var value = expression[name]?.GetValue<string>() ?? "none";
        return value is "none" or "block" or "blend"
            ? value
            : throw new InvalidVrmDataException($"The expression {name} value is invalid.");
    }

    private static string GetMorphTargetName(JsonObject mesh, int targetIndex)
    {
        if (mesh["extras"]?["targetNames"] is JsonArray names &&
            targetIndex >= 0 && targetIndex < names.Count &&
            names[targetIndex] is JsonValue name)
        {
            return name.GetValue<string>();
        }
        if (mesh["primitives"] is JsonArray primitives && primitives.Count > 0 &&
            primitives[0]?["extras"]?["targetNames"] is JsonArray primitiveNames &&
            targetIndex >= 0 && targetIndex < primitiveNames.Count &&
            primitiveNames[targetIndex] is JsonValue primitiveName)
        {
            return primitiveName.GetValue<string>();
        }
        return $"morphTarget_{targetIndex}";
    }

    private static void ValidateManifest(ExpressionPackManifest manifest, byte[] binary)
    {
        if (manifest.Source is null || manifest.RequiredFeatures is null || manifest.Binary is null ||
            manifest.MeshGroups is null || manifest.MorphTargets is null || manifest.Expressions is null)
        {
            throw new InvalidVrmDataException("The Expression Pack manifest is missing a required object or array.");
        }
        if (manifest.Format != FormatIdentifier || manifest.FormatVersion != 1)
        {
            throw new InvalidVrmDataException("The Expression Pack format identifier or version is unsupported.");
        }
        var isVrm1 = manifest.Source.VrmGeneration == "1.0" && manifest.Source.SpecVersion == "1.0";
        var isVrm0 = manifest.Source.VrmGeneration == "0.x" &&
            !string.IsNullOrWhiteSpace(manifest.Source.SpecVersion) &&
            manifest.Source.SpecVersion.StartsWith("0.", StringComparison.Ordinal);
        if (!isVrm1 && !isVrm0)
        {
            throw new InvalidVrmDataException("The Expression Pack source VRM generation is unsupported.");
        }
        if (manifest.RequiredFeatures.Any(feature => feature != "morph-target-binds"))
        {
            throw new InvalidVrmDataException("The Expression Pack requires unsupported features.");
        }
        if (manifest.Binary.Path != BinaryEntryName ||
            manifest.Binary.Format != "avatarunway-morph-buffer" ||
            manifest.Binary.FormatVersion != 1 ||
            manifest.Binary.ByteLength != binary.Length ||
            !string.Equals(manifest.Binary.Sha256, Sha256(binary), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidVrmDataException("The Expression Pack binary descriptor is invalid.");
        }
        if (binary.Length < 16 || !binary.AsSpan(0, 8).SequenceEqual(BinaryMagic) ||
            BinaryPrimitives.ReadUInt32LittleEndian(binary.AsSpan(8)) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(binary.AsSpan(12)) != 0)
        {
            throw new InvalidVrmDataException("The Expression Pack binary header is invalid.");
        }
        var requiredSourceKind = isVrm1 ? "vrm1-custom" : "vrm0-unknown";
        if (manifest.Expressions.Count == 0 || manifest.Expressions.Any(expression => expression.SourceKind != requiredSourceKind))
        {
            throw new InvalidVrmDataException("The Expression Pack contains no supported custom expressions.");
        }

        if (manifest.MeshGroups.Count > 1024 || manifest.MorphTargets.Count > 4096 || manifest.Expressions.Count > 4096 ||
            manifest.MeshGroups.Select(group => group.Id).Distinct(StringComparer.Ordinal).Count() != manifest.MeshGroups.Count ||
            manifest.MorphTargets.Select(morph => morph.Id).Distinct(StringComparer.Ordinal).Count() != manifest.MorphTargets.Count ||
            manifest.Expressions.Select(expression => expression.Name).Distinct(StringComparer.Ordinal).Count() != manifest.Expressions.Count)
        {
            throw new InvalidVrmDataException("The Expression Pack contains excessive or duplicate manifest identifiers.");
        }

        var groupById = manifest.MeshGroups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        var morphById = manifest.MorphTargets.ToDictionary(morph => morph.Id, StringComparer.Ordinal);
        foreach (var group in manifest.MeshGroups)
        {
            if (string.IsNullOrWhiteSpace(group.Id) || group.Primitives.Count == 0 || group.Primitives.Count > 4096 ||
                group.Primitives.Select(primitive => primitive.PrimitiveIndex).Distinct().Count() != group.Primitives.Count ||
                group.Primitives.Any(primitive => primitive.PrimitiveIndex < 0 || primitive.Fingerprint is null || primitive.Fingerprint.VertexCount < 0))
            {
                throw new InvalidVrmDataException("An Expression Pack mesh group is invalid.");
            }
        }
        foreach (var morph in manifest.MorphTargets)
        {
            if (string.IsNullOrWhiteSpace(morph.Id) || !groupById.TryGetValue(morph.MeshGroupId, out var group) ||
                morph.SourceTargetIndex < 0 || morph.Primitives.Count != group.Primitives.Count ||
                morph.Primitives.Select(primitive => primitive.PrimitiveIndex).Distinct().Count() != morph.Primitives.Count)
            {
                throw new InvalidVrmDataException("An Expression Pack Morph Target descriptor is invalid.");
            }
            foreach (var primitive in morph.Primitives)
            {
                var groupPrimitive = group.Primitives.SingleOrDefault(item => item.PrimitiveIndex == primitive.PrimitiveIndex);
                if (groupPrimitive is null || primitive.Attributes.Count == 0 ||
                    primitive.Attributes.Keys.Any(semantic => !MorphSemantics.Contains(semantic, StringComparer.Ordinal)) ||
                    primitive.Attributes.Values.Any(attribute => attribute.Count != groupPrimitive.Fingerprint.VertexCount))
                {
                    throw new InvalidVrmDataException("An Expression Pack Morph Target primitive is invalid.");
                }
            }
        }
        foreach (var expression in manifest.Expressions)
        {
            if (string.IsNullOrWhiteSpace(expression.Name) || expression.MorphTargetBinds.Count == 0 ||
                expression.OverrideBlink is not ("none" or "block" or "blend") ||
                expression.OverrideLookAt is not ("none" or "block" or "blend") ||
                expression.OverrideMouth is not ("none" or "block" or "blend") ||
                expression.MorphTargetBinds.Any(bind =>
                    !groupById.ContainsKey(bind.MeshGroupId) ||
                    !morphById.TryGetValue(bind.MorphTargetId, out var morph) ||
                    morph.MeshGroupId != bind.MeshGroupId ||
                    !float.IsFinite(bind.Weight) || bind.Weight < 0f || bind.Weight > 1f))
            {
                throw new InvalidVrmDataException("An Expression Pack custom expression descriptor is invalid.");
            }
        }

        var ranges = new List<(long Offset, long End)>();
        foreach (var attributeEntry in manifest.MorphTargets.SelectMany(morph => morph.Primitives).SelectMany(primitive => primitive.Attributes))
        {
            var semantic = attributeEntry.Key;
            var attribute = attributeEntry.Value;
            if (attribute.ComponentType != "FLOAT32" || attribute.Type != "VEC3" || attribute.Count <= 0 ||
                attribute.ByteOffset < 16 || attribute.ByteOffset % 4 != 0 ||
                attribute.ByteLength != checked(attribute.Count * 12L) ||
                attribute.ByteLength < 0 || attribute.ByteOffset > binary.Length ||
                attribute.ByteLength > binary.Length - attribute.ByteOffset ||
                (attribute.Min is not null && (attribute.Min.Length != 3 || attribute.Min.Any(value => !float.IsFinite(value)))) ||
                (attribute.Max is not null && (attribute.Max.Length != 3 || attribute.Max.Any(value => !float.IsFinite(value)))))
            {
                throw new InvalidVrmDataException("An Expression Pack binary attribute range is invalid.");
            }
            var bytes = binary.AsSpan(checked((int)attribute.ByteOffset), checked((int)attribute.ByteLength));
            if (!string.Equals(attribute.Sha256, Sha256(bytes), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidVrmDataException("An Expression Pack attribute hash does not match.");
            }
            var values = BytesToFiniteFloats(bytes);
            if (attribute.Min is not null && attribute.Max is not null &&
                attribute.Min.Where((minimum, component) => minimum > attribute.Max[component]).Any())
            {
                throw new InvalidVrmDataException("An Expression Pack attribute minimum is greater than its maximum.");
            }
            if (semantic == "POSITION")
            {
                ValidatePositionBounds(attribute, values);
            }
            ranges.Add((attribute.ByteOffset, attribute.ByteOffset + attribute.ByteLength));
        }
        ranges.Sort((left, right) => left.Offset.CompareTo(right.Offset));
        for (var index = 1; index < ranges.Count; index++)
        {
            if (ranges[index].Offset < ranges[index - 1].End)
            {
                throw new InvalidVrmDataException("Expression Pack binary attribute ranges overlap.");
            }
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, long maximumLength)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream(entry.Length <= int.MaxValue ? checked((int)entry.Length) : 0);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }
            total = checked(total + read);
            if (total > maximumLength || total > entry.Length)
            {
                throw new InvalidVrmDataException("An Expression Pack entry expanded beyond its declared or allowed length.");
            }
            memory.Write(buffer, 0, read);
        }
        if (total != entry.Length)
        {
            throw new InvalidVrmDataException("An Expression Pack entry length does not match its metadata.");
        }
        return memory.ToArray();
    }

    private static void ValidateEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.Contains('/') ||
            name.Contains(':') || name is "." or ".." || Path.IsPathRooted(name))
        {
            throw new InvalidVrmDataException("An Expression Pack entry name is unsafe.");
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes, CompressionLevel compressionLevel)
    {
        var entry = archive.CreateEntry(name, compressionLevel);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static byte[] FloatsToBytes(ReadOnlySpan<float> values)
    {
        var bytes = new byte[checked(values.Length * 4)];
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(index * 4), BitConverter.SingleToInt32Bits(values[index] == 0f ? 0f : values[index]));
        }
        return bytes;
    }

    private static float[] BytesToFiniteFloats(ReadOnlySpan<byte> bytes)
    {
        var values = new float[bytes.Length / 4];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[(index * 4)..]));
            if (!float.IsFinite(values[index]))
            {
                throw new InvalidVrmDataException("An Expression Pack Morph Target contains NaN or Infinity.");
            }
        }
        return values;
    }

    private static void ValidatePositionBounds(ExpressionPackAttribute attribute, ReadOnlySpan<float> values)
    {
        if (attribute.Min is null || attribute.Max is null || attribute.Min.Length != 3 || attribute.Max.Length != 3)
        {
            throw new InvalidVrmDataException("An Expression Pack POSITION attribute must define three-component min and max values.");
        }
        var (actualMin, actualMax) = CalculateMinMax(values);
        for (var component = 0; component < 3; component++)
        {
            if (attribute.Min[component] > attribute.Max[component] ||
                attribute.Min[component] != actualMin[component] ||
                attribute.Max[component] != actualMax[component])
            {
                throw new InvalidVrmDataException("An Expression Pack POSITION min or max does not match its binary values.");
            }
        }
    }

    private static (float[] Min, float[] Max) CalculateMinMax(ReadOnlySpan<float> values)
    {
        var min = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
        var max = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        for (var index = 0; index < values.Length; index++)
        {
            var component = index % 3;
            min[component] = Math.Min(min[component], values[index]);
            max[component] = Math.Max(max[component], values[index]);
        }
        return (min, max);
    }

    private static void Align4(MemoryStream stream)
    {
        while (stream.Position % 4 != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

public sealed record ExpressionPackReadLimits(
    int MaximumEntryCount,
    long MaximumManifestLength,
    long MaximumBinaryLength,
    long MaximumTotalLength,
    double MaximumCompressionRatio)
{
    public static ExpressionPackReadLimits Default { get; } = new(
        MaximumEntryCount: 16,
        MaximumManifestLength: 4L * 1024 * 1024,
        MaximumBinaryLength: 512L * 1024 * 1024,
        MaximumTotalLength: 516L * 1024 * 1024,
        MaximumCompressionRatio: 100d);
}
