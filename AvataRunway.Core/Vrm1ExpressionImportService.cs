using System.Text.Json.Nodes;

namespace AvataRunway.Core;

public enum ExpressionImportStatus
{
    New,
    AlreadyExists,
    NameConflict,
    Incompatible,
    Unsupported
}

public sealed record ExpressionImportItem(
    string Name,
    ExpressionImportStatus Status,
    string Reason,
    MeshCompatibilityGrade Compatibility);

public sealed record Vrm1ImportPlan(
    MeshCompatibilityGrade Compatibility,
    IReadOnlyList<ExpressionImportItem> Expressions,
    IReadOnlyDictionary<string, int> TargetMeshByGroupId);

public sealed class Vrm1ExpressionImportService
{
    private static readonly string[] SupportedMorphSemantics = ["POSITION", "NORMAL", "TANGENT"];
    private static readonly HashSet<string> ReservedPresetNames = new(StringComparer.Ordinal)
    {
        "happy", "angry", "sad", "relaxed", "surprised",
        "aa", "ih", "ou", "ee", "oh",
        "blink", "blinkLeft", "blinkRight",
        "lookUp", "lookDown", "lookLeft", "lookRight", "neutral"
    };
    private readonly MeshFingerprintService fingerprintService = new();

    public Vrm1ImportPlan Analyze(LoadedExpressionPack pack, GlbDocument target)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(target);
        var vrm = GetVrm1Extension(target.Root);
        var mappings = new Dictionary<string, int>(StringComparer.Ordinal);
        var mappingGrades = new Dictionary<string, MeshCompatibilityGrade>(StringComparer.Ordinal);
        var compatibility = MeshCompatibilityGrade.Exact;
        foreach (var group in pack.Manifest.MeshGroups)
        {
            var matches = FindMatchingMeshes(group, target);
            if (matches.Count != 1)
            {
                compatibility = MeshCompatibilityGrade.Incompatible;
                continue;
            }
            mappings.Add(group.Id, matches[0].MeshIndex);
            mappingGrades.Add(group.Id, matches[0].Grade);
            compatibility = Min(compatibility, matches[0].Grade);
        }

        if (mappings.Count != pack.Manifest.MeshGroups.Count)
        {
            compatibility = MeshCompatibilityGrade.Incompatible;
        }
        if (mappings.Values.Distinct().Count() != mappings.Count)
        {
            compatibility = MeshCompatibilityGrade.Incompatible;
        }

        var custom = vrm["expressions"]?["custom"]?.AsObject();
        var preset = vrm["expressions"]?["preset"]?.AsObject();
        var items = new List<ExpressionImportItem>();
        foreach (var expression in pack.Manifest.Expressions)
        {
            var hasSafeMappings = TryGetExpressionMappings(
                expression,
                mappings,
                mappingGrades,
                out var expressionMappings,
                out var expressionCompatibility);
            if (expression.SourceKind != "vrm1-custom")
            {
                items.Add(new ExpressionImportItem(expression.Name, ExpressionImportStatus.Unsupported, "Only VRM 1.0 custom expressions are supported.", expressionCompatibility));
            }
            else if (ReservedPresetNames.Contains(expression.Name))
            {
                items.Add(new ExpressionImportItem(expression.Name, ExpressionImportStatus.NameConflict, "VRM 1.0 preset expression names are reserved and cannot be used by custom expressions.", expressionCompatibility));
            }
            else if (!hasSafeMappings)
            {
                items.Add(new ExpressionImportItem(expression.Name, ExpressionImportStatus.Incompatible, "The target mesh identity could not be verified safely or the mesh mapping is not one-to-one.", expressionCompatibility));
            }
            else if (expression.MorphTargetBinds.Any(bind => CountNodesUsingMesh(target.Root, expressionMappings[bind.MeshGroupId]) != 1))
            {
                items.Add(new ExpressionImportItem(expression.Name, ExpressionImportStatus.Incompatible, "The target node mapping is ambiguous.", expressionCompatibility));
            }
            else if (ExpressionUsesMorphWeightAnimation(expression, target, expressionMappings))
            {
                items.Add(new ExpressionImportItem(expression.Name, ExpressionImportStatus.Unsupported, "The target mesh is controlled by a Morph Weight Animation.", expressionCompatibility));
            }
            else if (!HasCompatibleBaseMorphAttributes(expression, pack, target, expressionMappings))
            {
                items.Add(new ExpressionImportItem(expression.Name, ExpressionImportStatus.Incompatible, "The target primitive is missing a required base POSITION, NORMAL, or TANGENT attribute.", expressionCompatibility));
            }
            else if (custom?[expression.Name] is JsonObject existing)
            {
                items.Add(IsSameExpression(pack, expression, existing, target, expressionMappings)
                    ? new ExpressionImportItem(expression.Name, ExpressionImportStatus.AlreadyExists, "The same expression and Morph Target data already exist.", expressionCompatibility)
                    : new ExpressionImportItem(expression.Name, ExpressionImportStatus.NameConflict, "An expression with the same name has different content.", expressionCompatibility));
            }
            else if (preset?.ContainsKey(expression.Name) == true)
            {
                items.Add(new ExpressionImportItem(expression.Name, ExpressionImportStatus.NameConflict, "A preset expression with the same reserved name already exists.", expressionCompatibility));
            }
            else
            {
                items.Add(new ExpressionImportItem(expression.Name, ExpressionImportStatus.New, string.Empty, expressionCompatibility));
            }
        }

        return new Vrm1ImportPlan(compatibility, items, mappings);
    }

    public GlbDocument ImportNewExpressions(
        LoadedExpressionPack pack,
        GlbDocument target,
        bool geometryDifferencesConfirmed,
        IEnumerable<string>? selectedExpressionNames = null)
    {
        var plan = Analyze(pack, target);
        var availableNewNames = plan.Expressions
            .Where(item => item.Status == ExpressionImportStatus.New)
            .Select(item => item.Name)
            .ToHashSet(StringComparer.Ordinal);
        var newNames = selectedExpressionNames is null
            ? availableNewNames
            : selectedExpressionNames.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (!newNames.IsSubsetOf(availableNewNames))
        {
            throw new InvalidOperationException("Only explicitly selected expressions with New status can be imported.");
        }
        var selectedExpressions = pack.Manifest.Expressions
            .Where(expression => newNames.Contains(expression.Name))
            .ToArray();
        var selectedMappings = ResolveSelectedMeshMappings(pack, target, selectedExpressions, out var selectedCompatibility);
        if (selectedCompatibility == MeshCompatibilityGrade.Incompatible)
        {
            throw new InvalidVrmDataException("A selected expression is incompatible with the target VRM.");
        }
        if (selectedCompatibility == MeshCompatibilityGrade.CompatibleWithGeometryDifferences && !geometryDifferencesConfirmed)
        {
            throw new InvalidOperationException("Import requires explicit confirmation because base POSITION data differs.");
        }

        var candidate = target.DeepClone();
        var newSlots = new Dictionary<string, (int MeshIndex, int TargetIndex)>(StringComparer.Ordinal);
        var referencedMorphIds = selectedExpressions
            .SelectMany(expression => expression.MorphTargetBinds)
            .Select(bind => bind.MorphTargetId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var morph in pack.Manifest.MorphTargets.Where(morph => referencedMorphIds.Contains(morph.Id)))
        {
            if (!selectedMappings.TryGetValue(morph.MeshGroupId, out var targetMeshIndex))
            {
                throw new InvalidVrmDataException("A Morph Target references an unmapped mesh group.");
            }
            var targetIndex = AppendMorphTarget(candidate, pack.BinaryData, morph, targetMeshIndex);
            newSlots.Add(morph.Id, (targetMeshIndex, targetIndex));
        }

        var vrm = GetVrm1Extension(candidate.Root);
        var expressions = vrm["expressions"] as JsonObject;
        if (expressions is null)
        {
            expressions = new JsonObject();
            vrm["expressions"] = expressions;
        }
        var custom = expressions["custom"] as JsonObject;
        if (custom is null)
        {
            custom = new JsonObject();
            expressions["custom"] = custom;
        }
        foreach (var expression in pack.Manifest.Expressions)
        {
            if (!newNames.Contains(expression.Name))
            {
                continue;
            }
            var imported = new JsonObject
            {
                ["isBinary"] = expression.IsBinary,
                ["overrideBlink"] = expression.OverrideBlink,
                ["overrideLookAt"] = expression.OverrideLookAt,
                ["overrideMouth"] = expression.OverrideMouth
            };
            var binds = new JsonArray();
            foreach (var bind in expression.MorphTargetBinds)
            {
                if (!newSlots.TryGetValue(bind.MorphTargetId, out var slot))
                {
                    throw new InvalidVrmDataException("An expression references an unknown Morph Target.");
                }
                var nodes = GetNodesUsingMesh(candidate.Root, slot.MeshIndex);
                if (nodes.Count != 1)
                {
                    throw new InvalidVrmDataException("The imported expression target node mapping is ambiguous.");
                }
                binds.Add(new JsonObject
                {
                    ["node"] = nodes[0],
                    ["index"] = slot.TargetIndex,
                    ["weight"] = bind.Weight
                });
            }
            imported["morphTargetBinds"] = binds;
            custom.Add(expression.Name, imported);
        }

        var reparsed = GlbDocument.Parse(candidate.Write());
        _ = GetVrm1Extension(reparsed.Root);
        return reparsed;
    }

    private static bool TryGetExpressionMappings(
        ExpressionPackExpression expression,
        IReadOnlyDictionary<string, int> mappings,
        IReadOnlyDictionary<string, MeshCompatibilityGrade> mappingGrades,
        out IReadOnlyDictionary<string, int> expressionMappings,
        out MeshCompatibilityGrade compatibility)
    {
        var resolved = new Dictionary<string, int>(StringComparer.Ordinal);
        compatibility = MeshCompatibilityGrade.Exact;
        foreach (var groupId in expression.MorphTargetBinds.Select(bind => bind.MeshGroupId).Distinct(StringComparer.Ordinal))
        {
            if (!mappings.TryGetValue(groupId, out var meshIndex) || !mappingGrades.TryGetValue(groupId, out var grade))
            {
                expressionMappings = resolved;
                compatibility = MeshCompatibilityGrade.Incompatible;
                return false;
            }
            resolved.Add(groupId, meshIndex);
            compatibility = Min(compatibility, grade);
        }
        expressionMappings = resolved;
        if (resolved.Values.Distinct().Count() != resolved.Count)
        {
            compatibility = MeshCompatibilityGrade.Incompatible;
            return false;
        }
        return true;
    }

    private IReadOnlyDictionary<string, int> ResolveSelectedMeshMappings(
        LoadedExpressionPack pack,
        GlbDocument target,
        IEnumerable<ExpressionPackExpression> expressions,
        out MeshCompatibilityGrade compatibility)
    {
        var groupIds = expressions
            .SelectMany(expression => expression.MorphTargetBinds)
            .Select(bind => bind.MeshGroupId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var groupsById = pack.Manifest.MeshGroups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        var mappings = new Dictionary<string, int>(StringComparer.Ordinal);
        compatibility = MeshCompatibilityGrade.Exact;
        foreach (var groupId in groupIds)
        {
            if (!groupsById.TryGetValue(groupId, out var group))
            {
                throw new InvalidVrmDataException("A selected expression references an unmapped mesh group.");
            }
            var matches = FindMatchingMeshes(group, target);
            if (matches.Count != 1)
            {
                throw new InvalidVrmDataException("A selected expression mesh identity could not be verified safely.");
            }
            mappings.Add(groupId, matches[0].MeshIndex);
            compatibility = Min(compatibility, matches[0].Grade);
        }
        if (mappings.Values.Distinct().Count() != mappings.Count)
        {
            throw new InvalidVrmDataException("Selected expressions do not have a one-to-one source and target mesh mapping.");
        }
        return mappings;
    }

    private static bool ExpressionUsesMorphWeightAnimation(
        ExpressionPackExpression expression,
        GlbDocument target,
        IReadOnlyDictionary<string, int> mappings)
    {
        var meshIndices = expression.MorphTargetBinds
            .Select(bind => mappings[bind.MeshGroupId])
            .Distinct()
            .ToHashSet();
        if (target.Root["animations"] is not JsonArray animations)
        {
            return false;
        }

        foreach (var animationNode in animations)
        {
            if (animationNode?["channels"] is not JsonArray channels)
            {
                continue;
            }
            foreach (var channel in channels)
            {
                if (channel?["target"] is not JsonObject animationTarget ||
                    animationTarget["path"]?.GetValue<string>() != "weights" ||
                    animationTarget["node"] is not JsonValue nodeValue)
                {
                    continue;
                }
                var node = GltfAccessorReader.GetObject(target.Root, "nodes", nodeValue.GetValue<int>());
                if (node["mesh"] is JsonValue meshValue && meshIndices.Contains(meshValue.GetValue<int>()))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool HasCompatibleBaseMorphAttributes(
        ExpressionPackExpression expression,
        LoadedExpressionPack pack,
        GlbDocument target,
        IReadOnlyDictionary<string, int> mappings)
    {
        var morphById = pack.Manifest.MorphTargets.ToDictionary(morph => morph.Id, StringComparer.Ordinal);
        var reader = new GltfAccessorReader(target);
        foreach (var bind in expression.MorphTargetBinds)
        {
            var morph = morphById[bind.MorphTargetId];
            var meshIndex = mappings[bind.MeshGroupId];
            var mesh = GltfAccessorReader.GetObject(target.Root, "meshes", meshIndex);
            var primitives = mesh["primitives"]?.AsArray();
            if (primitives is null || primitives.Count != morph.Primitives.Count)
            {
                return false;
            }
            foreach (var packedPrimitive in morph.Primitives)
            {
                if (packedPrimitive.PrimitiveIndex < 0 || packedPrimitive.PrimitiveIndex >= primitives.Count ||
                    primitives[packedPrimitive.PrimitiveIndex]?["attributes"] is not JsonObject targetAttributes)
                {
                    return false;
                }
                foreach (var (semantic, morphAttribute) in packedPrimitive.Attributes)
                {
                    if (targetAttributes[semantic] is not JsonValue baseAccessorValue)
                    {
                        return false;
                    }
                    var baseInfo = reader.GetInfo(baseAccessorValue.GetValue<int>());
                    if (baseInfo.Count != morphAttribute.Count)
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }

    private static bool IsSameExpression(
        LoadedExpressionPack pack,
        ExpressionPackExpression packed,
        JsonObject existing,
        GlbDocument target,
        IReadOnlyDictionary<string, int> mappings)
    {
        if ((existing["isBinary"]?.GetValue<bool>() ?? false) != packed.IsBinary ||
            (existing["overrideBlink"]?.GetValue<string>() ?? "none") != packed.OverrideBlink ||
            (existing["overrideLookAt"]?.GetValue<string>() ?? "none") != packed.OverrideLookAt ||
            (existing["overrideMouth"]?.GetValue<string>() ?? "none") != packed.OverrideMouth)
        {
            return false;
        }
        var existingBinds = existing["morphTargetBinds"] as JsonArray;
        if (existingBinds is null || existingBinds.Count != packed.MorphTargetBinds.Count)
        {
            return false;
        }

        var unmatched = existingBinds.Select(node => node?.AsObject()).ToList();
        foreach (var packedBind in packed.MorphTargetBinds)
        {
            var morph = pack.Manifest.MorphTargets.Single(item => item.Id == packedBind.MorphTargetId);
            var meshIndex = mappings[packedBind.MeshGroupId];
            var matchIndex = unmatched.FindIndex(existingBind =>
                existingBind is not null &&
                Math.Abs((existingBind["weight"]?.GetValue<float>() ?? 1f) - packedBind.Weight) <= 0.000001f &&
                ExistingBindMatchesMorph(existingBind, morph, meshIndex, target));
            if (matchIndex < 0)
            {
                return false;
            }
            unmatched.RemoveAt(matchIndex);
        }
        return unmatched.Count == 0;
    }

    private static bool ExistingBindMatchesMorph(
        JsonObject bind,
        ExpressionPackMorphTarget morph,
        int expectedMeshIndex,
        GlbDocument target)
    {
        var nodeIndex = bind["node"]?.GetValue<int>() ?? -1;
        var targetIndex = bind["index"]?.GetValue<int>() ?? -1;
        if (nodeIndex < 0 || targetIndex < 0 ||
            GltfAccessorReader.GetObject(target.Root, "nodes", nodeIndex)["mesh"]?.GetValue<int>() != expectedMeshIndex)
        {
            return false;
        }

        return MorphSlotMatchesMorph(morph, expectedMeshIndex, targetIndex, target);
    }

    internal static bool MorphSlotMatchesMorph(
        ExpressionPackMorphTarget morph,
        int expectedMeshIndex,
        int targetIndex,
        GlbDocument target)
    {
        var mesh = GltfAccessorReader.GetObject(target.Root, "meshes", expectedMeshIndex);
        var primitives = mesh["primitives"]?.AsArray();
        if (primitives is null || primitives.Count != morph.Primitives.Count)
        {
            return false;
        }
        var reader = new GltfAccessorReader(target);
        for (var primitiveIndex = 0; primitiveIndex < primitives.Count; primitiveIndex++)
        {
            var targets = primitives[primitiveIndex]?["targets"] as JsonArray;
            if (targets is null || targetIndex < 0 || targetIndex >= targets.Count || targets[targetIndex] is not JsonObject targetObject)
            {
                return false;
            }
            var packedPrimitive = morph.Primitives.Single(item => item.PrimitiveIndex == primitiveIndex);
            var existingSemantics = targetObject.Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();
            var packedSemantics = packedPrimitive.Attributes.Keys.Order(StringComparer.Ordinal).ToArray();
            if (!existingSemantics.SequenceEqual(packedSemantics, StringComparer.Ordinal))
            {
                return false;
            }
            foreach (var semantic in packedSemantics)
            {
                if (targetObject[semantic] is not JsonValue accessorNode)
                {
                    return false;
                }
                var bytes = FloatsToBytes(reader.ReadFloat32(accessorNode.GetValue<int>()));
                if (!string.Equals(Sha256(bytes), packedPrimitive.Attributes[semantic].Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private List<(int MeshIndex, MeshCompatibilityGrade Grade)> FindMatchingMeshes(ExpressionPackMeshGroup source, GlbDocument target)
    {
        var meshes = target.Root["meshes"]?.AsArray()
            ?? throw new InvalidVrmDataException("The target meshes array is missing.");
        var matches = new List<(int, MeshCompatibilityGrade)>();
        for (var meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
        {
            var mesh = meshes[meshIndex]?.AsObject()
                ?? throw new InvalidVrmDataException("A target mesh definition is invalid.");
            var primitives = mesh["primitives"]?.AsArray()
                ?? throw new InvalidVrmDataException("A target mesh primitives array is missing.");
            if (primitives.Count != source.Primitives.Count)
            {
                continue;
            }
            var grade = MeshCompatibilityGrade.Exact;
            for (var primitiveIndex = 0; primitiveIndex < primitives.Count; primitiveIndex++)
            {
                var targetFingerprint = fingerprintService.Create(target, meshIndex, primitiveIndex);
                grade = Min(grade, fingerprintService.Compare(source.Primitives[primitiveIndex].Fingerprint, targetFingerprint));
            }
            if (grade != MeshCompatibilityGrade.Incompatible)
            {
                matches.Add((meshIndex, grade));
            }
        }
        return matches;
    }

    internal static int AppendMorphTarget(
        GlbDocument candidate,
        byte[] packBinary,
        ExpressionPackMorphTarget morph,
        int meshIndex)
    {
        var mesh = GltfAccessorReader.GetObject(candidate.Root, "meshes", meshIndex);
        var primitives = mesh["primitives"]?.AsArray()
            ?? throw new InvalidVrmDataException("A target mesh primitives array is missing.");
        if (morph.Primitives.Count != primitives.Count)
        {
            throw new InvalidVrmDataException("A Morph Target primitive count does not match the target mesh.");
        }

        var oldTargetCount = GetAndValidateTargetCount(primitives);
        for (var primitiveIndex = 0; primitiveIndex < primitives.Count; primitiveIndex++)
        {
            var primitive = primitives[primitiveIndex]!.AsObject();
            var packPrimitive = morph.Primitives.SingleOrDefault(item => item.PrimitiveIndex == primitiveIndex)
                ?? throw new InvalidVrmDataException("A Morph Target primitive descriptor is missing.");
            var target = new JsonObject();
            foreach (var semantic in SupportedMorphSemantics)
            {
                if (!packPrimitive.Attributes.TryGetValue(semantic, out var attribute))
                {
                    continue;
                }
                var bytes = packBinary.AsSpan(checked((int)attribute.ByteOffset), checked((int)attribute.ByteLength));
                var byteOffset = candidate.AppendBinary(bytes, 4);
                var bufferViews = GetOrCreateArray(candidate.Root, "bufferViews");
                var bufferViewIndex = bufferViews.Count;
                bufferViews.Add(new JsonObject
                {
                    ["buffer"] = 0,
                    ["byteOffset"] = byteOffset,
                    ["byteLength"] = bytes.Length
                });
                var accessors = GetOrCreateArray(candidate.Root, "accessors");
                var accessorIndex = accessors.Count;
                var accessor = new JsonObject
                {
                    ["bufferView"] = bufferViewIndex,
                    ["componentType"] = 5126,
                    ["count"] = attribute.Count,
                    ["type"] = "VEC3"
                };
                if (semantic == "POSITION")
                {
                    accessor["min"] = FloatArray(attribute.Min ?? throw new InvalidVrmDataException("A POSITION minimum is missing."));
                    accessor["max"] = FloatArray(attribute.Max ?? throw new InvalidVrmDataException("A POSITION maximum is missing."));
                }
                accessors.Add(accessor);
                target[semantic] = accessorIndex;
            }
            if (target.Count == 0)
            {
                throw new InvalidVrmDataException("An imported Morph Target contains no supported attributes.");
            }
            var targets = primitive["targets"] as JsonArray;
            if (targets is null)
            {
                targets = new JsonArray();
                primitive["targets"] = targets;
            }
            targets.Add(target);
        }

        AppendZeroWeight(mesh, "weights", oldTargetCount);
        foreach (var nodeIndex in GetNodesUsingMesh(candidate.Root, meshIndex))
        {
            AppendZeroWeight(GltfAccessorReader.GetObject(candidate.Root, "nodes", nodeIndex), "weights", oldTargetCount);
        }
        AppendTargetName(mesh, morph.Name, oldTargetCount);
        return oldTargetCount;
    }

    private static int GetAndValidateTargetCount(JsonArray primitives)
    {
        int? count = null;
        foreach (var node in primitives)
        {
            var primitive = node!.AsObject();
            var current = (primitive["targets"] as JsonArray)?.Count ?? 0;
            if (count is not null && count.Value != current)
            {
                throw new InvalidVrmDataException("All primitives must have the same Morph Target slot count.");
            }
            count = current;
        }
        return count ?? 0;
    }

    private static void AppendZeroWeight(JsonObject owner, string propertyName, int oldTargetCount)
    {
        if (owner[propertyName] is not JsonArray weights)
        {
            return;
        }
        if (weights.Count != oldTargetCount)
        {
            throw new InvalidVrmDataException($"{propertyName} length does not match the existing Morph Target count.");
        }
        weights.Add(0f);
    }

    private static void AppendTargetName(JsonObject mesh, string requestedName, int oldTargetCount)
    {
        var extras = mesh["extras"] as JsonObject;
        if (extras is null)
        {
            extras = new JsonObject();
            mesh["extras"] = extras;
        }
        var names = extras["targetNames"] as JsonArray;
        if (names is null)
        {
            names = new JsonArray();
            for (var index = 0; index < oldTargetCount; index++)
            {
                names.Add($"morphTarget_{index}");
            }
            extras["targetNames"] = names;
        }
        if (names.Count != oldTargetCount)
        {
            throw new InvalidVrmDataException("mesh.extras.targetNames length does not match the existing Morph Target count.");
        }
        var used = names.Select(node => node?.GetValue<string>() ?? string.Empty).ToHashSet(StringComparer.Ordinal);
        var name = requestedName;
        var suffix = 2;
        while (!used.Add(name))
        {
            name = $"{requestedName} (Imported {suffix++})";
        }
        names.Add(name);
    }

    private static JsonArray GetOrCreateArray(JsonObject root, string propertyName)
    {
        var array = root[propertyName] as JsonArray;
        if (array is null)
        {
            array = new JsonArray();
            root[propertyName] = array;
        }
        return array;
    }

    private static JsonArray FloatArray(IEnumerable<float> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }
        return array;
    }

    private static byte[] FloatsToBytes(ReadOnlySpan<float> values)
    {
        var bytes = new byte[checked(values.Length * 4)];
        for (var index = 0; index < values.Length; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(index * 4),
                BitConverter.SingleToInt32Bits(values[index] == 0f ? 0f : values[index]));
        }
        return bytes;
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    private static JsonObject GetVrm1Extension(JsonObject root)
    {
        var vrm = root["extensions"]?["VRMC_vrm"]?.AsObject()
            ?? throw new InvalidVrmDataException("The VRMC_vrm extension is missing.");
        if (vrm["specVersion"]?.GetValue<string>() != "1.0")
        {
            throw new InvalidVrmDataException("Only VRM 1.0 targets are supported.");
        }
        return vrm;
    }

    private static List<int> GetNodesUsingMesh(JsonObject root, int meshIndex)
    {
        var nodes = root["nodes"]?.AsArray()
            ?? throw new InvalidVrmDataException("The nodes array is missing.");
        var result = new List<int>();
        for (var index = 0; index < nodes.Count; index++)
        {
            if (nodes[index] is JsonObject node && node["mesh"]?.GetValue<int>() == meshIndex)
            {
                result.Add(index);
            }
        }
        return result;
    }

    private static int CountNodesUsingMesh(JsonObject root, int meshIndex) => GetNodesUsingMesh(root, meshIndex).Count;

    private static MeshCompatibilityGrade Min(MeshCompatibilityGrade left, MeshCompatibilityGrade right) =>
        (MeshCompatibilityGrade)Math.Min((int)left, (int)right);
}
