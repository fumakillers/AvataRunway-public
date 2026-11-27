using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace AvataRunway.Core.Tests;

public sealed class Vrm0ExpressionRoundTripTests
{
    [Fact]
    public void ExportImportReparse_AddsUnknownClipAndPreservesExistingData()
    {
        var sourceBytes = SyntheticVrmFixture.CreateVrm0(includeUnknownClip: true);
        var targetBytes = SyntheticVrmFixture.CreateVrm0(includeUnknownClip: false);
        var sourceHash = SHA256.HashData(sourceBytes);
        var targetHash = SHA256.HashData(targetBytes);
        var service = new ExpressionPackService();
        using var stream = new MemoryStream();
        service.ExportVrm0Unknown(GlbDocument.Parse(sourceBytes), ["AvataRunwayTest"], stream);
        stream.Position = 0;
        var pack = service.Read(stream);

        Assert.Equal("0.x", pack.Manifest.Source.VrmGeneration);
        Assert.Equal("vrm0-unknown", pack.Manifest.Expressions[0].SourceKind);
        Assert.Equal(0.75f, pack.Manifest.Expressions[0].MorphTargetBinds[0].Weight);

        var target = GlbDocument.Parse(targetBytes);
        var importer = new Vrm0ExpressionImportService();
        var plan = importer.Analyze(pack, target);
        Assert.Equal(MeshCompatibilityGrade.Exact, plan.Compatibility);
        Assert.Equal(ExpressionImportStatus.New, plan.Expressions[0].Status);

        var imported = importer.ImportNewExpressions(
            pack,
            target,
            geometryDifferencesConfirmed: false,
            selectedExpressionNames: ["AvataRunwayTest"]);
        var reparsed = GlbDocument.Parse(imported.Write());
        var groups = reparsed.Root["extensions"]!["VRM"]!["blendShapeMaster"]!["blendShapeGroups"]!.AsArray();
        Assert.Equal(2, groups.Count);
        Assert.Equal("joy", groups[0]!["presetName"]!.GetValue<string>());
        Assert.Equal("AvataRunwayTest", groups[1]!["name"]!.GetValue<string>());
        Assert.Equal("unknown", groups[1]!["presetName"]!.GetValue<string>());
        Assert.Equal(75f, groups[1]!["binds"]![0]!["weight"]!.GetValue<float>());
        Assert.All(reparsed.Root["meshes"]![0]!["primitives"]!.AsArray(), primitive =>
            Assert.Single(primitive!["targets"]!.AsArray()));
        Assert.Equal(42, reparsed.Root["extensions"]!["VENDOR_unknown"]!["preserveMe"]!.GetValue<int>());
        var unknownChunk = Assert.Single(reparsed.Chunks, chunk => chunk.Type == SyntheticVrmFixture.UnknownChunkType);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, unknownChunk.Data);
        Assert.Equal(sourceHash, SHA256.HashData(sourceBytes));
        Assert.Equal(targetHash, SHA256.HashData(targetBytes));
    }

    [Fact]
    public void Analyze_ExistingUnknownClip_DistinguishesSameContentAndConflict()
    {
        var sourceBytes = SyntheticVrmFixture.CreateVrm0(includeUnknownClip: true);
        var pack = CreatePack(sourceBytes);
        var existing = GlbDocument.Parse(sourceBytes);
        var importer = new Vrm0ExpressionImportService();

        Assert.Equal(ExpressionImportStatus.AlreadyExists, importer.Analyze(pack, existing).Expressions[0].Status);

        existing.Root["extensions"]!["VRM"]!["blendShapeMaster"]!["blendShapeGroups"]![1]!["binds"]![0]!["weight"] = 25f;
        Assert.Equal(ExpressionImportStatus.NameConflict, importer.Analyze(pack, existing).Expressions[0].Status);
    }

    [Fact]
    public void Export_RejectsPresetAndMaterialValueClip()
    {
        var service = new ExpressionPackService();
        var source = GlbDocument.Parse(SyntheticVrmFixture.CreateVrm0(includeUnknownClip: true));
        Assert.Throws<InvalidVrmDataException>(() => service.ExportVrm0Unknown(source, ["Joy"], new MemoryStream()));

        var unsupported = GlbDocument.Parse(SyntheticVrmFixture.CreateVrm0(includeUnknownClip: true, unsupportedMaterialBind: true));
        Assert.Throws<InvalidVrmDataException>(() => service.ExportVrm0Unknown(unsupported, ["AvataRunwayTest"], new MemoryStream()));
    }

    [Fact]
    public void Analyze_AppliesAnimationAndMorphSemanticSafetyRules()
    {
        var pack = CreatePack(SyntheticVrmFixture.CreateVrm0(includeUnknownClip: true, includeMorphNormal: true));
        var animated = GlbDocument.Parse(SyntheticVrmFixture.CreateVrm0(
            includeUnknownClip: false,
            includeMorphWeightAnimation: true));
        Assert.Equal(ExpressionImportStatus.Unsupported, new Vrm0ExpressionImportService().Analyze(pack, animated).Expressions[0].Status);

        var missingNormal = GlbDocument.Parse(SyntheticVrmFixture.CreateVrm0(
            includeUnknownClip: false,
            omitBaseNormal: true));
        Assert.Equal(ExpressionImportStatus.Incompatible, new Vrm0ExpressionImportService().Analyze(pack, missingNormal).Expressions[0].Status);
    }

    [Fact]
    public void Analyze_AppliesFingerprintGradesAndExplicitGeometryConfirmation()
    {
        var pack = CreatePack(SyntheticVrmFixture.CreateVrm0(includeUnknownClip: true));
        var geometryChanged = GlbDocument.Parse(SyntheticVrmFixture.CreateVrm0(
            includeUnknownClip: false,
            changePositions: true));
        var importer = new Vrm0ExpressionImportService();

        Assert.Equal(
            MeshCompatibilityGrade.CompatibleWithGeometryDifferences,
            importer.Analyze(pack, geometryChanged).Compatibility);
        Assert.Throws<InvalidOperationException>(() =>
            importer.ImportNewExpressions(pack, geometryChanged, geometryDifferencesConfirmed: false));
        Assert.NotNull(importer.ImportNewExpressions(pack, geometryChanged, geometryDifferencesConfirmed: true));

        var uvChanged = GlbDocument.Parse(SyntheticVrmFixture.CreateVrm0(
            includeUnknownClip: false,
            changeUv: true));
        Assert.Equal(MeshCompatibilityGrade.Incompatible, importer.Analyze(pack, uvChanged).Compatibility);
    }

    [Fact]
    public void Analyze_TwoSourceGroupsMappedToSameTargetMesh_IsIncompatible()
    {
        var pack = CreatePack(SyntheticVrmFixture.CreateVrm0(includeUnknownClip: true));
        var duplicateGroup = Clone(pack.Manifest.MeshGroups[0]);
        duplicateGroup.Id = "mesh-duplicate";
        pack.Manifest.MeshGroups.Add(duplicateGroup);
        var duplicateMorph = Clone(pack.Manifest.MorphTargets[0]);
        duplicateMorph.Id = "morph-duplicate";
        duplicateMorph.MeshGroupId = duplicateGroup.Id;
        pack.Manifest.MorphTargets.Add(duplicateMorph);
        pack.Manifest.Expressions[0].MorphTargetBinds.Add(new ExpressionPackMorphBind
        {
            MeshGroupId = duplicateGroup.Id,
            MorphTargetId = duplicateMorph.Id,
            SourceNodeIndex = -1,
            Weight = 0.5f
        });

        var plan = new Vrm0ExpressionImportService().Analyze(
            pack,
            GlbDocument.Parse(SyntheticVrmFixture.CreateVrm0(includeUnknownClip: false)));

        Assert.Equal(MeshCompatibilityGrade.Incompatible, plan.Compatibility);
        Assert.Equal(ExpressionImportStatus.Incompatible, plan.Expressions[0].Status);
    }

    [Fact]
    public void Import_TwoSelectedClipsMappedToSameTargetMesh_IsRejected()
    {
        var pack = CreatePack(SyntheticVrmFixture.CreateVrm0(includeUnknownClip: true));
        var duplicateGroup = Clone(pack.Manifest.MeshGroups[0]);
        duplicateGroup.Id = "mesh-duplicate";
        pack.Manifest.MeshGroups.Add(duplicateGroup);
        var duplicateMorph = Clone(pack.Manifest.MorphTargets[0]);
        duplicateMorph.Id = "morph-duplicate";
        duplicateMorph.MeshGroupId = duplicateGroup.Id;
        pack.Manifest.MorphTargets.Add(duplicateMorph);
        var secondExpression = Clone(pack.Manifest.Expressions[0]);
        secondExpression.Name = "SecondClip";
        secondExpression.MorphTargetBinds[0].MeshGroupId = duplicateGroup.Id;
        secondExpression.MorphTargetBinds[0].MorphTargetId = duplicateMorph.Id;
        pack.Manifest.Expressions.Add(secondExpression);
        var target = GlbDocument.Parse(SyntheticVrmFixture.CreateVrm0(includeUnknownClip: false));
        var importer = new Vrm0ExpressionImportService();

        Assert.All(importer.Analyze(pack, target).Expressions, item => Assert.Equal(ExpressionImportStatus.New, item.Status));
        Assert.Throws<InvalidVrmDataException>(() => importer.ImportNewExpressions(
            pack,
            target,
            geometryDifferencesConfirmed: false,
            selectedExpressionNames: ["AvataRunwayTest", "SecondClip"]));
    }

    [Fact]
    public void AnalyzeAndImport_IgnoreUnselectedClipWithIncompatibleMeshGroup()
    {
        var pack = CreatePack(SyntheticVrmFixture.CreateVrm0(includeUnknownClip: true));
        AddExpressionWithIncompatibleMeshGroup(pack, "IncompatibleClip");
        var target = GlbDocument.Parse(SyntheticVrmFixture.CreateVrm0(includeUnknownClip: false));
        var importer = new Vrm0ExpressionImportService();

        var plan = importer.Analyze(pack, target);

        Assert.Equal(MeshCompatibilityGrade.Incompatible, plan.Compatibility);
        var compatibleItem = Assert.Single(plan.Expressions, item => item.Name == "AvataRunwayTest");
        Assert.Equal(ExpressionImportStatus.New, compatibleItem.Status);
        Assert.Equal(MeshCompatibilityGrade.Exact, compatibleItem.Compatibility);
        var incompatibleItem = Assert.Single(plan.Expressions, item => item.Name == "IncompatibleClip");
        Assert.Equal(ExpressionImportStatus.Incompatible, incompatibleItem.Status);

        var imported = importer.ImportNewExpressions(
            pack,
            target,
            geometryDifferencesConfirmed: false,
            selectedExpressionNames: ["AvataRunwayTest"]);

        var groups = imported.Root["extensions"]!["VRM"]!["blendShapeMaster"]!["blendShapeGroups"]!.AsArray();
        Assert.Contains(groups, group => group?["name"]?.GetValue<string>() == "AvataRunwayTest");
        Assert.DoesNotContain(groups, group => group?["name"]?.GetValue<string>() == "IncompatibleClip");
        Assert.All(imported.Root["meshes"]![0]!["primitives"]!.AsArray(), primitive =>
            Assert.Single(primitive!["targets"]!.AsArray()));
    }

    [Fact]
    public void Import_GeometryConfirmationIsScopedToSelectedClip()
    {
        var pack = CreatePack(SyntheticVrmFixture.CreateVrm0(includeUnknownClip: true));
        AddExpressionWithGeometryDifferenceMeshGroup(pack, "GeometryClip");
        var target = GlbDocument.Parse(SyntheticVrmFixture.CreateVrm0(includeUnknownClip: false));
        var importer = new Vrm0ExpressionImportService();
        var plan = importer.Analyze(pack, target);

        Assert.Equal(MeshCompatibilityGrade.Exact, Assert.Single(plan.Expressions, item => item.Name == "AvataRunwayTest").Compatibility);
        Assert.Equal(
            MeshCompatibilityGrade.CompatibleWithGeometryDifferences,
            Assert.Single(plan.Expressions, item => item.Name == "GeometryClip").Compatibility);
        Assert.NotNull(importer.ImportNewExpressions(
            pack,
            target,
            geometryDifferencesConfirmed: false,
            selectedExpressionNames: ["AvataRunwayTest"]));
        Assert.Throws<InvalidOperationException>(() => importer.ImportNewExpressions(
            pack,
            target,
            geometryDifferencesConfirmed: false,
            selectedExpressionNames: ["GeometryClip"]));
    }

    private static LoadedExpressionPack CreatePack(byte[] sourceBytes)
    {
        var service = new ExpressionPackService();
        using var stream = new MemoryStream();
        service.ExportVrm0Unknown(GlbDocument.Parse(sourceBytes), ["AvataRunwayTest"], stream);
        stream.Position = 0;
        return service.Read(stream);
    }

    private static void AddExpressionWithIncompatibleMeshGroup(LoadedExpressionPack pack, string expressionName)
    {
        var group = Clone(pack.Manifest.MeshGroups[0]);
        group.Id = "mesh-incompatible";
        group.Primitives[0].Fingerprint = group.Primitives[0].Fingerprint with
        {
            TopologySha256 = new string('0', 64)
        };
        pack.Manifest.MeshGroups.Add(group);

        var morph = Clone(pack.Manifest.MorphTargets[0]);
        morph.Id = "morph-incompatible";
        morph.MeshGroupId = group.Id;
        pack.Manifest.MorphTargets.Add(morph);

        var expression = Clone(pack.Manifest.Expressions[0]);
        expression.Name = expressionName;
        foreach (var bind in expression.MorphTargetBinds)
        {
            bind.MeshGroupId = group.Id;
            bind.MorphTargetId = morph.Id;
        }
        pack.Manifest.Expressions.Add(expression);
    }

    private static void AddExpressionWithGeometryDifferenceMeshGroup(LoadedExpressionPack pack, string expressionName)
    {
        var group = Clone(pack.Manifest.MeshGroups[0]);
        group.Id = "mesh-geometry-difference";
        group.Primitives[0].Fingerprint = group.Primitives[0].Fingerprint with
        {
            PositionSha256 = new string('0', 64)
        };
        pack.Manifest.MeshGroups.Add(group);

        var morph = Clone(pack.Manifest.MorphTargets[0]);
        morph.Id = "morph-geometry-difference";
        morph.MeshGroupId = group.Id;
        pack.Manifest.MorphTargets.Add(morph);

        var expression = Clone(pack.Manifest.Expressions[0]);
        expression.Name = expressionName;
        expression.MorphTargetBinds[0].MeshGroupId = group.Id;
        expression.MorphTargetBinds[0].MorphTargetId = morph.Id;
        pack.Manifest.Expressions.Add(expression);
    }

    private static T Clone<T>(T value) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
