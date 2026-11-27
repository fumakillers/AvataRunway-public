using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AvataRunway.Core.Tests;

public sealed class Vrm1ExpressionRoundTripTests
{
    [Fact]
    public void ExportImportReparse_PreservesTargetAndAddsCustomExpression()
    {
        var sourceBytes = SyntheticVrmFixture.CreateVrm1(includeCustomExpression: true);
        var originalTargetBytes = SyntheticVrmFixture.CreateVrm1(includeCustomExpression: false);
        var originalTargetHash = SHA256.HashData(originalTargetBytes);
        var source = GlbDocument.Parse(sourceBytes);
        var target = GlbDocument.Parse(originalTargetBytes);
        var originalTargetBinary = target.BinaryBuffer.ToArray();
        var originalAccessorCount = target.Root["accessors"]!.AsArray().Count;
        var originalBufferViewCount = target.Root["bufferViews"]!.AsArray().Count;
        Assert.InRange(
            Assert.Single(target.Chunks, chunk => chunk.Type == GlbDocument.BinChunkType).Data.Length - target.BinaryLogicalLength,
            0,
            3);
        var service = new ExpressionPackService();

        using var packStream = new MemoryStream();
        service.ExportVrm1Custom(source, ["AvataRunwayTest"], packStream);
        packStream.Position = 0;
        var pack = service.Read(packStream);
        var importer = new Vrm1ExpressionImportService();
        var plan = importer.Analyze(pack, target);

        Assert.Equal(MeshCompatibilityGrade.Exact, plan.Compatibility);
        Assert.Single(plan.Expressions);
        Assert.Equal(ExpressionImportStatus.New, plan.Expressions[0].Status);

        var imported = importer.ImportNewExpressions(pack, target, geometryDifferencesConfirmed: false);
        var outputBytes = imported.Write();
        var reparsed = GlbDocument.Parse(outputBytes);

        Assert.Equal(originalTargetHash, SHA256.HashData(originalTargetBytes));
        Assert.True(reparsed.BinaryBuffer.AsSpan(0, originalTargetBinary.Length).SequenceEqual(originalTargetBinary));
        Assert.Equal(reparsed.BinaryLogicalLength, reparsed.Root["buffers"]![0]!["byteLength"]!.GetValue<int>());
        Assert.InRange(GetBinChunkPadding(outputBytes, reparsed.BinaryLogicalLength), 0, 3);

        var mesh = reparsed.Root["meshes"]![0]!.AsObject();
        var primitives = mesh["primitives"]!.AsArray();
        Assert.All(primitives, primitiveNode => Assert.Single(primitiveNode!["targets"]!.AsArray()));
        var primitive = primitives[0]!.AsObject();
        Assert.Single(mesh["weights"]!.AsArray());
        Assert.Equal(0f, mesh["weights"]![0]!.GetValue<float>());
        var node = reparsed.Root["nodes"]![0]!.AsObject();
        Assert.Single(node["weights"]!.AsArray());
        Assert.Equal(0f, node["weights"]![0]!.GetValue<float>());

        var custom = reparsed.Root["extensions"]!["VRMC_vrm"]!["expressions"]!["custom"]!.AsObject();
        var expression = custom["AvataRunwayTest"]!.AsObject();
        Assert.Equal("blend", expression["overrideBlink"]!.GetValue<string>());
        Assert.Equal("block", expression["overrideMouth"]!.GetValue<string>());
        Assert.Equal(0.75f, expression["morphTargetBinds"]![0]!["weight"]!.GetValue<float>());
        Assert.Equal(42, reparsed.Root["extensions"]!["VENDOR_unknown"]!["preserveMe"]!.GetValue<int>());
        var unknown = Assert.Single(reparsed.Chunks, chunk => chunk.Type == SyntheticVrmFixture.UnknownChunkType);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, unknown.Data);

        var accessors = reparsed.Root["accessors"]!.AsArray();
        var importedAccessorIndex = primitive["targets"]![0]!["POSITION"]!.GetValue<int>();
        var importedAccessor = accessors[importedAccessorIndex]!.AsObject();
        var importedView = reparsed.Root["bufferViews"]![importedAccessor["bufferView"]!.GetValue<int>()]!.AsObject();
        Assert.Equal(0, importedView["byteOffset"]!.GetValue<int>() % 4);
        Assert.Equal(0, importedAccessor["byteOffset"]?.GetValue<int>() ?? 0);
        Assert.Equal(3, importedAccessor["count"]!.GetValue<int>());
        Assert.All(
            reparsed.Root["bufferViews"]!.AsArray().Skip(originalBufferViewCount),
            view => Assert.Equal(0, view!["byteOffset"]!.GetValue<int>() % 4));
        Assert.All(
            reparsed.Root["accessors"]!.AsArray().Skip(originalAccessorCount),
            accessor => Assert.Equal(0, accessor!["byteOffset"]?.GetValue<int>() ?? 0));
    }

    [Fact]
    public void Analyze_PositionDifferenceWithStrongIdentity_RequiresExplicitConfirmation()
    {
        var pack = CreatePack(SyntheticVrmFixture.CreateVrm1(includeCustomExpression: true));
        var target = GlbDocument.Parse(SyntheticVrmFixture.CreateVrm1(includeCustomExpression: false, changePositions: true));
        var importer = new Vrm1ExpressionImportService();

        var plan = importer.Analyze(pack, target);

        Assert.Equal(MeshCompatibilityGrade.CompatibleWithGeometryDifferences, plan.Compatibility);
        Assert.Throws<InvalidOperationException>(() => importer.ImportNewExpressions(pack, target, geometryDifferencesConfirmed: false));
        var imported = importer.ImportNewExpressions(pack, target, geometryDifferencesConfirmed: true);
        Assert.NotNull(imported.Root["extensions"]!["VRMC_vrm"]!["expressions"]!["custom"]!["AvataRunwayTest"]);
    }

    [Fact]
    public void Analyze_UvDifference_IsIncompatibleEvenWhenVertexCountMatches()
    {
        var pack = CreatePack(SyntheticVrmFixture.CreateVrm1(includeCustomExpression: true));
        var target = GlbDocument.Parse(SyntheticVrmFixture.CreateVrm1(includeCustomExpression: false, changeUv: true));

        var plan = new Vrm1ExpressionImportService().Analyze(pack, target);

        Assert.Equal(MeshCompatibilityGrade.Incompatible, plan.Compatibility);
        Assert.Equal(ExpressionImportStatus.Incompatible, plan.Expressions[0].Status);
    }

    [Fact]
    public void Analyze_SameExpressionAndMorphData_IsAlreadyExists()
    {
        var sourceBytes = SyntheticVrmFixture.CreateVrm1(includeCustomExpression: true);
        var pack = CreatePack(sourceBytes);
        var target = GlbDocument.Parse(sourceBytes);

        var plan = new Vrm1ExpressionImportService().Analyze(pack, target);

        Assert.Equal(MeshCompatibilityGrade.Exact, plan.Compatibility);
        Assert.Equal(ExpressionImportStatus.AlreadyExists, plan.Expressions[0].Status);
        var skipped = new Vrm1ExpressionImportService().ImportNewExpressions(pack, target, geometryDifferencesConfirmed: false);
        Assert.Single(skipped.Root["meshes"]![0]!["primitives"]![0]!["targets"]!.AsArray());
    }

    [Fact]
    public void Analyze_SameNameWithDifferentWeight_IsNameConflict()
    {
        var sourceBytes = SyntheticVrmFixture.CreateVrm1(includeCustomExpression: true);
        var pack = CreatePack(sourceBytes);
        var target = GlbDocument.Parse(sourceBytes);
        target.Root["extensions"]!["VRMC_vrm"]!["expressions"]!["custom"]!["AvataRunwayTest"]!["morphTargetBinds"]![0]!["weight"] = 0.25f;

        var plan = new Vrm1ExpressionImportService().Analyze(pack, target);

        Assert.Equal(ExpressionImportStatus.NameConflict, plan.Expressions[0].Status);
    }

    [Fact]
    public void Import_OnlyAddsExplicitlySelectedNewExpressions()
    {
        var source = GlbDocument.Parse(SyntheticVrmFixture.CreateVrm1(includeCustomExpression: true));
        var custom = source.Root["extensions"]!["VRMC_vrm"]!["expressions"]!["custom"]!.AsObject();
        custom["SecondCustom"] = custom["AvataRunwayTest"]!.DeepClone();
        var service = new ExpressionPackService();
        using var stream = new MemoryStream();
        service.ExportVrm1Custom(source, ["AvataRunwayTest", "SecondCustom"], stream);
        stream.Position = 0;
        var pack = service.Read(stream);
        var target = GlbDocument.Parse(SyntheticVrmFixture.CreateVrm1(includeCustomExpression: false));

        var imported = new Vrm1ExpressionImportService().ImportNewExpressions(
            pack,
            target,
            geometryDifferencesConfirmed: false,
            selectedExpressionNames: ["SecondCustom"]);

        var importedCustom = imported.Root["extensions"]!["VRMC_vrm"]!["expressions"]!["custom"]!.AsObject();
        Assert.Null(importedCustom["AvataRunwayTest"]);
        Assert.NotNull(importedCustom["SecondCustom"]);
        Assert.Single(imported.Root["meshes"]![0]!["primitives"]![0]!["targets"]!.AsArray());
    }

    [Fact]
    public void AnalyzeAndImport_IgnoreUnselectedExpressionWithIncompatibleMeshGroup()
    {
        var pack = CreatePack(SyntheticVrmFixture.CreateVrm1(includeCustomExpression: true));
        AddExpressionWithIncompatibleMeshGroup(pack, "IncompatibleCustom");
        var target = GlbDocument.Parse(SyntheticVrmFixture.CreateVrm1(includeCustomExpression: false));
        var importer = new Vrm1ExpressionImportService();

        var plan = importer.Analyze(pack, target);

        Assert.Equal(MeshCompatibilityGrade.Incompatible, plan.Compatibility);
        var compatibleItem = Assert.Single(plan.Expressions, item => item.Name == "AvataRunwayTest");
        Assert.Equal(ExpressionImportStatus.New, compatibleItem.Status);
        Assert.Equal(MeshCompatibilityGrade.Exact, compatibleItem.Compatibility);
        var incompatibleItem = Assert.Single(plan.Expressions, item => item.Name == "IncompatibleCustom");
        Assert.Equal(ExpressionImportStatus.Incompatible, incompatibleItem.Status);

        var imported = importer.ImportNewExpressions(
            pack,
            target,
            geometryDifferencesConfirmed: false,
            selectedExpressionNames: ["AvataRunwayTest"]);

        var custom = imported.Root["extensions"]!["VRMC_vrm"]!["expressions"]!["custom"]!.AsObject();
        Assert.NotNull(custom["AvataRunwayTest"]);
        Assert.Null(custom["IncompatibleCustom"]);
        Assert.All(imported.Root["meshes"]![0]!["primitives"]!.AsArray(), primitive =>
            Assert.Single(primitive!["targets"]!.AsArray()));
    }

    [Fact]
    public void Import_GeometryConfirmationIsScopedToSelectedExpression()
    {
        var pack = CreatePack(SyntheticVrmFixture.CreateVrm1(includeCustomExpression: true));
        AddExpressionWithGeometryDifferenceMeshGroup(pack, "GeometryCustom");
        var target = GlbDocument.Parse(SyntheticVrmFixture.CreateVrm1(includeCustomExpression: false));
        var importer = new Vrm1ExpressionImportService();
        var plan = importer.Analyze(pack, target);

        Assert.Equal(MeshCompatibilityGrade.Exact, Assert.Single(plan.Expressions, item => item.Name == "AvataRunwayTest").Compatibility);
        Assert.Equal(
            MeshCompatibilityGrade.CompatibleWithGeometryDifferences,
            Assert.Single(plan.Expressions, item => item.Name == "GeometryCustom").Compatibility);
        Assert.NotNull(importer.ImportNewExpressions(
            pack,
            target,
            geometryDifferencesConfirmed: false,
            selectedExpressionNames: ["AvataRunwayTest"]));
        Assert.Throws<InvalidOperationException>(() => importer.ImportNewExpressions(
            pack,
            target,
            geometryDifferencesConfirmed: false,
            selectedExpressionNames: ["GeometryCustom"]));
    }

    [Fact]
    public void Analyze_TargetMorphWeightAnimation_IsUnsupportedBeforeImport()
    {
        var pack = CreatePack(SyntheticVrmFixture.CreateVrm1(includeCustomExpression: true));
        var target = GlbDocument.Parse(SyntheticVrmFixture.CreateVrm1(
            includeCustomExpression: false,
            includeMorphWeightAnimation: true));

        var plan = new Vrm1ExpressionImportService().Analyze(pack, target);

        Assert.Equal(ExpressionImportStatus.Unsupported, plan.Expressions[0].Status);
        Assert.Contains("Animation", plan.Expressions[0].Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Analyze_MorphSemanticWithoutMatchingBaseAttribute_IsIncompatible(bool normal, bool tangent)
    {
        var source = SyntheticVrmFixture.CreateVrm1(
            includeCustomExpression: true,
            includeMorphNormal: normal,
            includeMorphTangent: tangent);
        var target = SyntheticVrmFixture.CreateVrm1(
            includeCustomExpression: false,
            omitBaseNormal: normal,
            omitBaseTangent: tangent);

        var plan = new Vrm1ExpressionImportService().Analyze(CreatePack(source), GlbDocument.Parse(target));

        Assert.Equal(ExpressionImportStatus.Incompatible, plan.Expressions[0].Status);
        Assert.Contains(normal ? "NORMAL" : "TANGENT", plan.Expressions[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_TwoSourceGroupsMappedToSameTargetMesh_IsIncompatible()
    {
        var pack = CreatePack(SyntheticVrmFixture.CreateVrm1(includeCustomExpression: true));
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
            SourceNodeIndex = 0,
            Weight = 0.5f
        });

        var plan = new Vrm1ExpressionImportService().Analyze(
            pack,
            GlbDocument.Parse(SyntheticVrmFixture.CreateVrm1(includeCustomExpression: false)));

        Assert.Equal(MeshCompatibilityGrade.Incompatible, plan.Compatibility);
        Assert.Equal(ExpressionImportStatus.Incompatible, plan.Expressions[0].Status);
    }

    [Fact]
    public void Import_TwoSelectedExpressionsMappedToSameTargetMesh_IsRejected()
    {
        var pack = CreatePack(SyntheticVrmFixture.CreateVrm1(includeCustomExpression: true));
        var duplicateGroup = Clone(pack.Manifest.MeshGroups[0]);
        duplicateGroup.Id = "mesh-duplicate";
        pack.Manifest.MeshGroups.Add(duplicateGroup);
        var duplicateMorph = Clone(pack.Manifest.MorphTargets[0]);
        duplicateMorph.Id = "morph-duplicate";
        duplicateMorph.MeshGroupId = duplicateGroup.Id;
        pack.Manifest.MorphTargets.Add(duplicateMorph);
        var secondExpression = Clone(pack.Manifest.Expressions[0]);
        secondExpression.Name = "SecondCustom";
        secondExpression.MorphTargetBinds[0].MeshGroupId = duplicateGroup.Id;
        secondExpression.MorphTargetBinds[0].MorphTargetId = duplicateMorph.Id;
        pack.Manifest.Expressions.Add(secondExpression);
        var target = GlbDocument.Parse(SyntheticVrmFixture.CreateVrm1(includeCustomExpression: false));
        var importer = new Vrm1ExpressionImportService();

        Assert.All(importer.Analyze(pack, target).Expressions, item => Assert.Equal(ExpressionImportStatus.New, item.Status));
        Assert.Throws<InvalidVrmDataException>(() => importer.ImportNewExpressions(
            pack,
            target,
            geometryDifferencesConfirmed: false,
            selectedExpressionNames: ["AvataRunwayTest", "SecondCustom"]));
    }

    [Fact]
    public void Analyze_ReservedPresetName_IsNameConflictEvenWhenTargetPresetIsAbsent()
    {
        var pack = CreatePack(SyntheticVrmFixture.CreateVrm1(includeCustomExpression: true));
        pack.Manifest.Expressions[0].Name = "sad";
        var target = GlbDocument.Parse(SyntheticVrmFixture.CreateVrm1(includeCustomExpression: false));
        Assert.Null(target.Root["extensions"]!["VRMC_vrm"]!["expressions"]!["preset"]!["sad"]);

        var plan = new Vrm1ExpressionImportService().Analyze(pack, target);

        Assert.Equal(ExpressionImportStatus.NameConflict, plan.Expressions[0].Status);
    }

    [Fact]
    public void Export_RejectsUnsupportedBindAndPresetSelection()
    {
        var service = new ExpressionPackService();
        var unsupported = GlbDocument.Parse(SyntheticVrmFixture.CreateVrm1(true, unsupportedMaterialBind: true));
        Assert.Throws<InvalidVrmDataException>(() => service.ExportVrm1Custom(unsupported, ["AvataRunwayTest"], new MemoryStream()));

        var source = GlbDocument.Parse(SyntheticVrmFixture.CreateVrm1(true));
        Assert.Throws<InvalidVrmDataException>(() => service.ExportVrm1Custom(source, ["happy"], new MemoryStream()));
    }

    [Fact]
    public void PackReader_RejectsUnsafeUnknownAndDuplicateEntries()
    {
        var service = new ExpressionPackService();
        using var unsafePack = CreateZip(("../manifest.json", new byte[] { 1 }), ("morphs.bin", new byte[] { 2 }));
        Assert.Throws<InvalidVrmDataException>(() => service.Read(unsafePack));

        using var duplicatePack = CreateZip(("manifest.json", new byte[] { 1 }), ("MANIFEST.JSON", new byte[] { 2 }), ("morphs.bin", new byte[] { 3 }));
        Assert.Throws<InvalidVrmDataException>(() => service.Read(duplicatePack));
    }

    [Fact]
    public void PackReader_EnforcesEntryExpandedSizeTotalSizeAndCompressionRatioLimits()
    {
        var bytes = CreatePackBytes();
        var service = new ExpressionPackService();

        Assert.Throws<InvalidVrmDataException>(() => ReadWithLimits(bytes, new ExpressionPackReadLimits(1, 4 * 1024 * 1024, 512 * 1024 * 1024, 516 * 1024 * 1024, 100)));
        Assert.Throws<InvalidVrmDataException>(() => ReadWithLimits(bytes, new ExpressionPackReadLimits(16, 1, 512 * 1024 * 1024, 516 * 1024 * 1024, 100)));
        Assert.Throws<InvalidVrmDataException>(() => ReadWithLimits(bytes, new ExpressionPackReadLimits(16, 4 * 1024 * 1024, 512 * 1024 * 1024, 1, 100)));
        Assert.Throws<InvalidVrmDataException>(() => ReadWithLimits(bytes, new ExpressionPackReadLimits(16, 4 * 1024 * 1024, 512 * 1024 * 1024, 516 * 1024 * 1024, 1)));
    }

    [Fact]
    public void PackReader_RejectsTruncatedZipAsDomainValidationError()
    {
        var bytes = CreatePackBytes();
        Array.Resize(ref bytes, bytes.Length - 12);
        using var stream = new MemoryStream(bytes);

        Assert.Throws<InvalidVrmDataException>(() => new ExpressionPackService().Read(stream));
    }

    [Fact]
    public void PackReader_RejectsMissingRequiredManifestObjectAsDomainValidationError()
    {
        var pack = CreatePack(SyntheticVrmFixture.CreateVrm1(includeCustomExpression: true));
        pack.Manifest.Source = null!;
        using var stream = WritePack(pack);

        Assert.Throws<InvalidVrmDataException>(() => new ExpressionPackService().Read(stream));
    }

    [Theory]
    [InlineData("nan")]
    [InlineData("positiveInfinity")]
    [InlineData("negativeInfinity")]
    public void PackReader_RejectsNonFiniteMorphFloat(string kind)
    {
        var pack = CreatePack(SyntheticVrmFixture.CreateVrm1(includeCustomExpression: true));
        var attribute = pack.Manifest.MorphTargets[0].Primitives[0].Attributes["POSITION"];
        var value = kind switch
        {
            "nan" => float.NaN,
            "positiveInfinity" => float.PositiveInfinity,
            "negativeInfinity" => float.NegativeInfinity,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            pack.BinaryData.AsSpan(checked((int)attribute.ByteOffset), 4),
            BitConverter.SingleToInt32Bits(value));
        RefreshPackHashes(pack, attribute);

        using var stream = WritePack(pack);
        Assert.Throws<InvalidVrmDataException>(() => new ExpressionPackService().Read(stream));
    }

    [Theory]
    [InlineData("missingMin")]
    [InlineData("missingMax")]
    [InlineData("mismatch")]
    [InlineData("minGreaterThanMax")]
    public void PackReader_RejectsInvalidPositionBounds(string kind)
    {
        var pack = CreatePack(SyntheticVrmFixture.CreateVrm1(includeCustomExpression: true));
        var attribute = pack.Manifest.MorphTargets[0].Primitives[0].Attributes["POSITION"];
        switch (kind)
        {
            case "missingMin":
                attribute.Min = null;
                break;
            case "missingMax":
                attribute.Max = null;
                break;
            case "mismatch":
                attribute.Min![0] -= 1f;
                break;
            case "minGreaterThanMax":
                attribute.Min![0] = attribute.Max![0] + 1f;
                break;
        }

        using var stream = WritePack(pack);
        Assert.Throws<InvalidVrmDataException>(() => new ExpressionPackService().Read(stream));
    }

    private static LoadedExpressionPack CreatePack(byte[] sourceBytes)
    {
        var service = new ExpressionPackService();
        using var stream = new MemoryStream();
        service.ExportVrm1Custom(GlbDocument.Parse(sourceBytes), ["AvataRunwayTest"], stream);
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

    private static byte[] CreatePackBytes()
    {
        var service = new ExpressionPackService();
        using var stream = new MemoryStream();
        service.ExportVrm1Custom(
            GlbDocument.Parse(SyntheticVrmFixture.CreateVrm1(includeCustomExpression: true)),
            ["AvataRunwayTest"],
            stream);
        return stream.ToArray();
    }

    private static LoadedExpressionPack ReadWithLimits(byte[] bytes, ExpressionPackReadLimits limits)
    {
        using var stream = new MemoryStream(bytes);
        return new ExpressionPackService().Read(stream, limits);
    }

    private static T Clone<T>(T value) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    private static void RefreshPackHashes(LoadedExpressionPack pack, ExpressionPackAttribute attribute)
    {
        attribute.Sha256 = Convert.ToHexStringLower(SHA256.HashData(
            pack.BinaryData.AsSpan(checked((int)attribute.ByteOffset), checked((int)attribute.ByteLength))));
        pack.Manifest.Binary.Sha256 = Convert.ToHexStringLower(SHA256.HashData(pack.BinaryData));
    }

    private static MemoryStream WritePack(LoadedExpressionPack pack)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = archive.CreateEntry(ExpressionPackService.ManifestEntryName, CompressionLevel.Optimal);
            using (var manifestStream = manifestEntry.Open())
            {
                JsonSerializer.Serialize(manifestStream, pack.Manifest, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
            }
            var binaryEntry = archive.CreateEntry(ExpressionPackService.BinaryEntryName, CompressionLevel.Optimal);
            using var binaryStream = binaryEntry.Open();
            binaryStream.Write(pack.BinaryData);
        }
        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateZip(params (string Name, byte[] Data)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entryData in entries)
            {
                var entry = archive.CreateEntry(entryData.Name);
                using var entryStream = entry.Open();
                entryStream.Write(entryData.Data);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static int GetBinChunkPadding(byte[] glb, int logicalLength)
    {
        var offset = 12;
        while (offset < glb.Length)
        {
            var length = BitConverter.ToInt32(glb, offset);
            var type = BitConverter.ToUInt32(glb, offset + 4);
            if (type == GlbDocument.BinChunkType)
            {
                return length - logicalLength;
            }
            offset += 8 + length;
        }
        throw new Xunit.Sdk.XunitException("BIN chunk not found.");
    }
}
