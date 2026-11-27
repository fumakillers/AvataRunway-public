using System.Text;
using Xunit;

namespace AvataRunway.Core.Tests;

public sealed class PoseFileServiceTests
{
    private readonly PoseFileService service = new();

    [Fact]
    public void SerializeDeserialize_NormalizesQuaternionAndRoundTrips()
    {
        var source = CreatePose(("hips", new PoseQuaternion { X = 0, Y = 0, Z = 0, W = 2 }));

        var bytes = service.Serialize(source);
        var result = service.Deserialize(bytes);

        Assert.Equal(PoseFileService.FormatIdentifier, result.Format);
        Assert.Equal(PoseFileService.CurrentVersion, result.Version);
        Assert.Equal(1.0, result.Bones["hips"].Rotation.W, 12);
        Assert.Equal(bytes, service.Serialize(result));
    }

    [Fact]
    public void SerializeDeserialize_PreservesHandAndFingerHumanoidBones()
    {
        var source = CreatePose(
            ("leftHand", new PoseQuaternion { X = 0.1, Y = 0.2, Z = 0.3, W = 0.9 }),
            ("rightThumbMetacarpal", new PoseQuaternion { X = 0.2, W = 0.98 }),
            ("leftIndexProximal", new PoseQuaternion { Y = 0.3, W = 0.95 }),
            ("leftIndexIntermediate", new PoseQuaternion { Z = 0.4, W = 0.92 }),
            ("leftIndexDistal", new PoseQuaternion { X = 0.25, W = 0.97 }));

        var result = service.Deserialize(service.Serialize(source));

        Assert.Equal(source.Bones.Keys.Order(), result.Bones.Keys.Order());
        Assert.All(result.Bones.Values, bone =>
        {
            var rotation = bone.Rotation;
            var length = Math.Sqrt(
                rotation.X * rotation.X +
                rotation.Y * rotation.Y +
                rotation.Z * rotation.Z +
                rotation.W * rotation.W);
            Assert.Equal(1, length, 12);
        });
    }

    [Theory]
    [InlineData("format", "Wrong")]
    [InlineData("rotationSpace", "world")]
    [InlineData("eulerOrder", "ZYX")]
    public void Deserialize_RejectsUnsupportedStringHeader(string property, string value)
    {
        var json = ValidJson.Replace(
            $"\"{property}\": \"{HeaderValue(property)}\"",
            $"\"{property}\": \"{value}\"",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => service.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Deserialize_RejectsUnsupportedVersion()
    {
        var json = ValidJson.Replace("\"version\": 1", "\"version\": 2", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => service.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Deserialize_Version1DefaultsToNeutralHipsAndUpgradesOnSerialize()
    {
        var legacy = service.Deserialize(Encoding.UTF8.GetBytes(ValidJson));

        Assert.Equal(PoseFileService.LegacyVersion, legacy.Version);
        Assert.Equal(0, legacy.HipsTranslation.X);
        Assert.Equal(0, legacy.HipsTranslation.Y);
        Assert.Equal(0, legacy.HipsTranslation.Z);

        var upgraded = service.Deserialize(service.Serialize(legacy));
        Assert.Equal(PoseFileService.CurrentVersion, upgraded.Version);
        Assert.Equal(PoseFileService.TranslationSpaceIdentifier, upgraded.TranslationSpace);
    }

    [Fact]
    public void SerializeDeserialize_PreservesFiniteNormalizedHipsTranslation()
    {
        var source = CreatePose(("hips", new PoseQuaternion()));
        source.HipsTranslation = new PoseVector3 { X = 0.125, Y = -0.25, Z = 0.0625 };

        var result = service.Deserialize(service.Serialize(source));

        Assert.Equal(0.125, result.HipsTranslation.X, 12);
        Assert.Equal(-0.25, result.HipsTranslation.Y, 12);
        Assert.Equal(0.0625, result.HipsTranslation.Z, 12);
    }

    [Fact]
    public void Deserialize_RejectsMissingOrInvalidVersion2Translation()
    {
        const string version2 = """
            {
              "format": "AvataRunwayPose",
              "version": 2,
              "rotationSpace": "normalized-local-delta",
              "eulerOrder": "XYZ",
              "translationSpace": "normalized-hips-local-height-ratio",
              "hipsTranslation": { "x": 0.0, "y": 0.0, "z": 0.0 },
              "bones": {}
            }
            """;
        Assert.Throws<InvalidDataException>(() => service.Deserialize(
            Encoding.UTF8.GetBytes(version2.Replace(
                "\"hipsTranslation\": { \"x\": 0.0, \"y\": 0.0, \"z\": 0.0 },",
                string.Empty,
                StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => service.Deserialize(
            Encoding.UTF8.GetBytes(version2.Replace(
                "\"normalized-hips-local-height-ratio\"",
                "\"world\"",
                StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => service.Deserialize(
            Encoding.UTF8.GetBytes(version2.Replace(
                "\"x\": 0.0",
                "\"x\": 1e999",
                StringComparison.Ordinal))));
    }

    [Fact]
    public void Serialize_RejectsNonFiniteHipsTranslation()
    {
        var pose = CreatePose(("hips", new PoseQuaternion()));
        pose.HipsTranslation.X = double.NaN;
        Assert.Throws<InvalidDataException>(() => service.Serialize(pose));
    }

    [Theory]
    [InlineData("\"NaN\"")]
    [InlineData("1e999")]
    public void Deserialize_RejectsNonFiniteQuaternion(string invalidNumber)
    {
        var json = ValidJson.Replace("\"x\": 0.0", $"\"x\": {invalidNumber}", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => service.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Deserialize_RejectsZeroQuaternion()
    {
        var json = ValidJson.Replace("\"w\": 1.0", "\"w\": 0.0", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => service.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Deserialize_RejectsMissingQuaternionComponent()
    {
        var json = ValidJson.Replace("\"x\": 0.0,", string.Empty, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => service.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Deserialize_RejectsInvalidJsonWithoutProducingPose()
    {
        PoseFile? currentPose = CreatePose(("hips", new PoseQuaternion()));

        Assert.Throws<InvalidDataException>(() => service.Deserialize("{"u8.ToArray()));

        Assert.NotNull(currentPose);
        Assert.Single(currentPose.Bones);
    }

    [Fact]
    public void Deserialize_PreservesUnknownBoneForCallerToIgnoreSafely()
    {
        var json = ValidJson.Replace("\"hips\"", "\"futureBone\"", StringComparison.Ordinal);
        var pose = service.Deserialize(Encoding.UTF8.GetBytes(json));
        Assert.True(pose.Bones.ContainsKey("futureBone"));
    }

    [Fact]
    public void Deserialize_RejectsOversizedInput()
    {
        var bytes = new byte[PoseFileService.MaximumFileSizeBytes + 1];
        Assert.Throws<InvalidDataException>(() => service.Deserialize(bytes));
    }

    [Fact]
    public async Task LoadAsync_RejectsOversizedFileBeforeParsing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oversized-{Guid.NewGuid():N}.avpose");
        try
        {
            await File.WriteAllBytesAsync(path, new byte[PoseFileService.MaximumFileSizeBytes + 1]);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Serialize_RejectsNaNInfinityAndTinyQuaternion()
    {
        Assert.Throws<InvalidDataException>(() =>
            service.Serialize(CreatePose(("hips", new PoseQuaternion { X = double.NaN, W = 1 }))));
        Assert.Throws<InvalidDataException>(() =>
            service.Serialize(CreatePose(("hips", new PoseQuaternion { X = double.PositiveInfinity, W = 1 }))));
        Assert.Throws<InvalidDataException>(() =>
            service.Serialize(CreatePose(("hips", new PoseQuaternion { W = 1e-8 }))));
    }

    private static PoseFile CreatePose(params (string Name, PoseQuaternion Rotation)[] bones)
    {
        var pose = new PoseFile();
        foreach (var (name, rotation) in bones)
        {
            pose.Bones.Add(name, new PoseBone { Rotation = rotation });
        }
        return pose;
    }

    private static string HeaderValue(string property) => property switch
    {
        "format" => PoseFileService.FormatIdentifier,
        "rotationSpace" => PoseFileService.RotationSpaceIdentifier,
        "eulerOrder" => PoseFileService.EulerOrderIdentifier,
        _ => throw new ArgumentOutOfRangeException(nameof(property))
    };

    private const string ValidJson = """
        {
          "format": "AvataRunwayPose",
          "version": 1,
          "rotationSpace": "normalized-local-delta",
          "eulerOrder": "XYZ",
          "bones": {
            "hips": {
              "rotation": {
                "x": 0.0,
                "y": 0.0,
                "z": 0.0,
                "w": 1.0
              }
            }
          }
        }
        """;
}
