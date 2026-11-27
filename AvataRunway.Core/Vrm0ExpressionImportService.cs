using System.Text.Json.Nodes;

namespace AvataRunway.Core;

public sealed record Vrm0ImportPlan(
    MeshCompatibilityGrade Compatibility,
    IReadOnlyList<ExpressionImportItem> Expressions,
    IReadOnlyDictionary<string, int> TargetMeshByGroupId);

public sealed class Vrm0ExpressionImportService
{
    private readonly MeshFingerprintService fingerprintService = new();

    public Vrm0ImportPlan Analyze(LoadedExpressionPack pack, GlbDocument target)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(target);
        var vrm = GetVrm0Extension(target.Root);
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
        if (mappings.Count != pack.Manifest.MeshGroups.Count || mappings.Values.Distinct().Count() != mappings.Count)
        {
            compatibility = MeshCompatibilityGrade.Incompatible;
        }

        var targetGroups = vrm["blendShapeMaster"]?["blendShapeGroups"] as JsonArray ?? new JsonArray();
        var items = new List<ExpressionImportItem>();
        foreach (var expression in pack.Manifest.Expressions)
        {
            var hasSafeMappings = TryGetExpressionMappings(
                expression,
                mappings,
                mappingGrades,
                out var expressionMappings,
                out var expressionCompatibility);
            if (expression.SourceKind != "vrm0-unknown")
            {
                items.Add(new ExpressionImportItem(expression.Name, ExpressionImportStatus.Unsupported, "Only VRM 0.x Unknown BlendShapeClips are supported.", expressionCompatibility));
            }
            else if (!hasSafeMappings)
            {
                items.Add(new ExpressionImportItem(expression.Name, ExpressionImportStatus.Incompatible, "The target mesh identity could not be verified safely or the mesh mapping is not one-to-one.", expressionCompatibility));
            }
            else if (ExpressionUsesMorphWeightAnimation(expression, target, expressionMappings))
            {
                items.Add(new ExpressionImportItem(expression.Name, ExpressionImportStatus.Unsupported, "The target mesh is controlled by a Morph Weight Animation.", expressionCompatibility));
            }
            else if (!HasCompatibleBaseMorphAttributes(expression, pack, target, expressionMappings))
            {
                items.Add(new ExpressionImportItem(expression.Name, ExpressionImportStatus.Incompatible, "The target primitive is missing a required base POSITION, NORMAL, or TANGENT attribute.", expressionCompatibility));
            }
            else
            {
                var sameName = targetGroups
                    .OfType<JsonObject>()
                    .Where(group => string.Equals(group["name"]?.GetValue<string>(), expression.Name, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (sameName.Length == 0)
                {
                    items.Add(new ExpressionImportItem(expression.Name, ExpressionImportStatus.New, string.Empty, expressionCompatibility));
                }
                else if (sameName.Length == 1 && IsSameExpression(pack, expression, sameName[0], target, expressionMappings))
                {
                    items.Add(new ExpressionImportItem(expression.Name, ExpressionImportStatus.AlreadyExists, "The same Unknown BlendShapeClip and Morph Target data already exist.", expressionCompatibility));
                }
                else
                {
                    items.Add(new ExpressionImportItem(expression.Name, ExpressionImportStatus.NameConflict, "A BlendShapeClip with the same name has different content.", expressionCompatibility));
                }
            }
        }
        return new Vrm0ImportPlan(compatibility, items, mappings);
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
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedNames = selectedExpressionNames is null
            ? availableNewNames
            : selectedExpressionNames.Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!selectedNames.IsSubsetOf(availableNewNames))
        {
            throw new InvalidOperationException("Only explicitly selected BlendShapeClips with New status can be imported.");
        }
        var selectedExpressions = pack.Manifest.Expressions
            .Where(expression => selectedNames.Contains(expression.Name))
            .ToArray();
        var selectedMappings = ResolveSelectedMeshMappings(pack, target, selectedExpressions, out var selectedCompatibility);
        if (selectedCompatibility == MeshCompatibilityGrade.Incompatible)
        {
            throw new InvalidVrmDataException("A selected BlendShapeClip is incompatible with the target VRM.");
        }
        if (selectedCompatibility == MeshCompatibilityGrade.CompatibleWithGeometryDifferences && !geometryDifferencesConfirmed)
        {
            throw new InvalidOperationException("Import requires explicit confirmation because base POSITION data differs.");
        }

        var candidate = target.DeepClone();
        var referencedMorphIds = selectedExpressions
            .SelectMany(expression => expression.MorphTargetBinds)
            .Select(bind => bind.MorphTargetId)
            .ToHashSet(StringComparer.Ordinal);
        var newSlots = new Dictionary<string, (int MeshIndex, int TargetIndex)>(StringComparer.Ordinal);
        foreach (var morph in pack.Manifest.MorphTargets.Where(morph => referencedMorphIds.Contains(morph.Id)))
        {
            var meshIndex = selectedMappings[morph.MeshGroupId];
            newSlots.Add(morph.Id, (meshIndex, Vrm1ExpressionImportService.AppendMorphTarget(candidate, pack.BinaryData, morph, meshIndex)));
        }

        var vrm = GetVrm0Extension(candidate.Root);
        var master = vrm["blendShapeMaster"] as JsonObject;
        if (master is null)
        {
            master = new JsonObject();
            vrm["blendShapeMaster"] = master;
        }
        var groups = master["blendShapeGroups"] as JsonArray;
        if (groups is null)
        {
            groups = new JsonArray();
            master["blendShapeGroups"] = groups;
        }
        foreach (var expression in pack.Manifest.Expressions.Where(expression => selectedNames.Contains(expression.Name)))
        {
            var binds = new JsonArray();
            foreach (var bind in expression.MorphTargetBinds)
            {
                var slot = newSlots[bind.MorphTargetId];
                binds.Add(new JsonObject
                {
                    ["mesh"] = slot.MeshIndex,
                    ["index"] = slot.TargetIndex,
                    ["weight"] = bind.Weight * 100f
                });
            }
            groups.Add(new JsonObject
            {
                ["name"] = expression.Name,
                ["presetName"] = "unknown",
                ["isBinary"] = expression.IsBinary,
                ["binds"] = binds,
                ["materialValues"] = new JsonArray()
            });
        }

        var reparsed = GlbDocument.Parse(candidate.Write());
        _ = GetVrm0Extension(reparsed.Root);
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
                throw new InvalidVrmDataException("A selected BlendShapeClip references an unmapped mesh group.");
            }
            var matches = FindMatchingMeshes(group, target);
            if (matches.Count != 1)
            {
                throw new InvalidVrmDataException("A selected BlendShapeClip mesh identity could not be verified safely.");
            }
            mappings.Add(groupId, matches[0].MeshIndex);
            compatibility = Min(compatibility, matches[0].Grade);
        }
        if (mappings.Values.Distinct().Count() != mappings.Count)
        {
            throw new InvalidVrmDataException("Selected BlendShapeClips do not have a one-to-one source and target mesh mapping.");
        }
        return mappings;
    }

    private List<(int MeshIndex, MeshCompatibilityGrade Grade)> FindMatchingMeshes(ExpressionPackMeshGroup source, GlbDocument target)
    {
        var meshes = target.Root["meshes"]?.AsArray()
            ?? throw new InvalidVrmDataException("The target meshes array is missing.");
        var matches = new List<(int, MeshCompatibilityGrade)>();
        for (var meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
        {
            var primitives = meshes[meshIndex]?["primitives"]?.AsArray()
                ?? throw new InvalidVrmDataException("A target mesh primitives array is missing.");
            if (primitives.Count != source.Primitives.Count)
            {
                continue;
            }
            var grade = MeshCompatibilityGrade.Exact;
            for (var primitiveIndex = 0; primitiveIndex < primitives.Count; primitiveIndex++)
            {
                grade = Min(grade, fingerprintService.Compare(
                    source.Primitives[primitiveIndex].Fingerprint,
                    fingerprintService.Create(target, meshIndex, primitiveIndex)));
            }
            if (grade != MeshCompatibilityGrade.Incompatible)
            {
                matches.Add((meshIndex, grade));
            }
        }
        return matches;
    }

    private static bool IsSameExpression(
        LoadedExpressionPack pack,
        ExpressionPackExpression packed,
        JsonObject existing,
        GlbDocument target,
        IReadOnlyDictionary<string, int> mappings)
    {
        if (!string.Equals(existing["presetName"]?.GetValue<string>(), "unknown", StringComparison.OrdinalIgnoreCase) ||
            (existing["isBinary"]?.GetValue<bool>() ?? false) != packed.IsBinary ||
            existing["materialValues"] is JsonArray materialValues && materialValues.Count > 0)
        {
            return false;
        }
        var existingBinds = existing["binds"] as JsonArray;
        if (existingBinds is null || existingBinds.Count != packed.MorphTargetBinds.Count)
        {
            return false;
        }
        var unmatched = existingBinds.Select(node => node?.AsObject()).ToList();
        foreach (var packedBind in packed.MorphTargetBinds)
        {
            var morph = pack.Manifest.MorphTargets.Single(item => item.Id == packedBind.MorphTargetId);
            var meshIndex = mappings[packedBind.MeshGroupId];
            var matchIndex = unmatched.FindIndex(bind =>
                bind is not null &&
                bind["mesh"]?.GetValue<int>() == meshIndex &&
                Math.Abs((bind["weight"]?.GetValue<float>() ?? 100f) / 100f - packedBind.Weight) <= 0.000001f &&
                Vrm1ExpressionImportService.MorphSlotMatchesMorph(
                    morph,
                    meshIndex,
                    bind["index"]?.GetValue<int>() ?? -1,
                    target));
            if (matchIndex < 0)
            {
                return false;
            }
            unmatched.RemoveAt(matchIndex);
        }
        return unmatched.Count == 0;
    }

    private static bool ExpressionUsesMorphWeightAnimation(
        ExpressionPackExpression expression,
        GlbDocument target,
        IReadOnlyDictionary<string, int> mappings)
    {
        var meshes = expression.MorphTargetBinds.Select(bind => mappings[bind.MeshGroupId]).ToHashSet();
        if (target.Root["animations"] is not JsonArray animations)
        {
            return false;
        }
        foreach (var channel in animations.OfType<JsonObject>().SelectMany(animation => animation["channels"]?.AsArray() ?? []))
        {
            if (channel?["target"] is JsonObject animationTarget &&
                animationTarget["path"]?.GetValue<string>() == "weights" &&
                animationTarget["node"] is JsonValue nodeValue)
            {
                var node = GltfAccessorReader.GetObject(target.Root, "nodes", nodeValue.GetValue<int>());
                if (node["mesh"] is JsonValue meshValue && meshes.Contains(meshValue.GetValue<int>()))
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
        var morphs = pack.Manifest.MorphTargets.ToDictionary(morph => morph.Id, StringComparer.Ordinal);
        var reader = new GltfAccessorReader(target);
        foreach (var bind in expression.MorphTargetBinds)
        {
            var morph = morphs[bind.MorphTargetId];
            var primitives = GltfAccessorReader.GetObject(target.Root, "meshes", mappings[bind.MeshGroupId])["primitives"]?.AsArray();
            if (primitives is null || primitives.Count != morph.Primitives.Count)
            {
                return false;
            }
            foreach (var packedPrimitive in morph.Primitives)
            {
                if (primitives[packedPrimitive.PrimitiveIndex]?["attributes"] is not JsonObject attributes)
                {
                    return false;
                }
                foreach (var (semantic, morphAttribute) in packedPrimitive.Attributes)
                {
                    if (attributes[semantic] is not JsonValue accessor || reader.GetInfo(accessor.GetValue<int>()).Count != morphAttribute.Count)
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }

    private static JsonObject GetVrm0Extension(JsonObject root)
    {
        var vrm = root["extensions"]?["VRM"]?.AsObject()
            ?? throw new InvalidVrmDataException("The VRM 0.x extension is missing.");
        var version = vrm["specVersion"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(version) || !version.StartsWith("0.", StringComparison.Ordinal))
        {
            throw new InvalidVrmDataException("Only VRM 0.x targets are supported.");
        }
        return vrm;
    }

    private static MeshCompatibilityGrade Min(MeshCompatibilityGrade left, MeshCompatibilityGrade right) =>
        (MeshCompatibilityGrade)Math.Min((int)left, (int)right);
}
