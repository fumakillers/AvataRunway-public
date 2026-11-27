using System.Text.Json;

namespace AvataRunway.Core;

public sealed class PoseFileService
{
    public const string FormatIdentifier = "AvataRunwayPose";
    public const int LegacyVersion = 1;
    public const int CurrentVersion = 2;
    public const string RotationSpaceIdentifier = "normalized-local-delta";
    public const string EulerOrderIdentifier = "XYZ";
    public const string TranslationSpaceIdentifier = "normalized-hips-local-height-ratio";
    public const int MaximumFileSizeBytes = 1024 * 1024;

    private const double MinimumQuaternionLengthSquared = 1e-12;
    public byte[] Serialize(PoseFile pose)
    {
        ArgumentNullException.ThrowIfNull(pose);
        var normalized = ValidateAndNormalize(pose);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(normalized, PoseFileJsonContext.Default.PoseFile);
        if (bytes.Length > MaximumFileSizeBytes)
        {
            throw new InvalidDataException($"The pose file exceeds the {MaximumFileSizeBytes} byte limit.");
        }
        return bytes;
    }

    public PoseFile Deserialize(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length > MaximumFileSizeBytes)
        {
            throw new InvalidDataException($"The pose file exceeds the {MaximumFileSizeBytes} byte limit.");
        }

        try
        {
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            return ParseAndNormalize(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The pose file contains invalid JSON.", exception);
        }
    }

    public async Task<PoseFile> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("The pose file does not exist.", path);
        }
        if (fileInfo.Length > MaximumFileSizeBytes)
        {
            throw new InvalidDataException($"The pose file exceeds the {MaximumFileSizeBytes} byte limit.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return Deserialize(bytes);
    }

    private static PoseFile ParseAndNormalize(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The pose file root must be an object.");
        }

        var pose = new PoseFile
        {
            Format = ReadRequiredString(root, "format"),
            Version = ReadRequiredInt32(root, "version"),
            RotationSpace = ReadRequiredString(root, "rotationSpace"),
            EulerOrder = ReadRequiredString(root, "eulerOrder"),
            Bones = new Dictionary<string, PoseBone>(StringComparer.Ordinal)
        };

        ValidateHeader(pose);
        if (pose.Version >= CurrentVersion)
        {
            pose.TranslationSpace = ReadRequiredString(root, "translationSpace");
            if (!string.Equals(
                pose.TranslationSpace,
                TranslationSpaceIdentifier,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Unsupported translation space '{pose.TranslationSpace}'.");
            }
            if (!root.TryGetProperty("hipsTranslation", out var translationElement) ||
                translationElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The pose file hipsTranslation value must be an object.");
            }
            pose.HipsTranslation = ReadFiniteVector3(translationElement, "hipsTranslation");
        }
        else
        {
            pose.TranslationSpace = TranslationSpaceIdentifier;
            pose.HipsTranslation = new PoseVector3();
        }
        if (!root.TryGetProperty("bones", out var bonesElement) ||
            bonesElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The pose file bones value must be an object.");
        }

        foreach (var boneProperty in bonesElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(boneProperty.Name))
            {
                throw new InvalidDataException("A pose bone name is empty.");
            }
            if (boneProperty.Value.ValueKind != JsonValueKind.Object ||
                !boneProperty.Value.TryGetProperty("rotation", out var rotationElement) ||
                rotationElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Bone '{boneProperty.Name}' has no valid rotation object.");
            }

            if (!pose.Bones.TryAdd(boneProperty.Name, new PoseBone
            {
                Rotation = NormalizeQuaternion(
                    ReadRequiredDouble(rotationElement, "x", boneProperty.Name),
                    ReadRequiredDouble(rotationElement, "y", boneProperty.Name),
                    ReadRequiredDouble(rotationElement, "z", boneProperty.Name),
                    ReadRequiredDouble(rotationElement, "w", boneProperty.Name),
                    boneProperty.Name)
            }))
            {
                throw new InvalidDataException($"Bone '{boneProperty.Name}' is duplicated.");
            }
        }

        return pose;
    }

    private static PoseFile ValidateAndNormalize(PoseFile pose)
    {
        ValidateHeader(pose);
        if (pose.Bones is null)
        {
            throw new InvalidDataException("The pose file bones value is required.");
        }

        if (pose.Version >= CurrentVersion && pose.HipsTranslation is null)
        {
            throw new InvalidDataException("The pose file hipsTranslation value is required.");
        }
        var hipsTranslation = pose.HipsTranslation ?? new PoseVector3();
        var result = new PoseFile
        {
            Version = CurrentVersion,
            TranslationSpace = TranslationSpaceIdentifier,
            HipsTranslation = ValidateVector3(hipsTranslation, "hipsTranslation")
        };
        foreach (var (boneName, bone) in pose.Bones)
        {
            if (string.IsNullOrWhiteSpace(boneName))
            {
                throw new InvalidDataException("A pose bone name is empty.");
            }
            if (bone?.Rotation is null)
            {
                throw new InvalidDataException($"Bone '{boneName}' has no rotation.");
            }

            var rotation = bone.Rotation;
            result.Bones.Add(boneName, new PoseBone
            {
                Rotation = NormalizeQuaternion(
                    rotation.X,
                    rotation.Y,
                    rotation.Z,
                    rotation.W,
                    boneName)
            });
        }
        return result;
    }

    private static void ValidateHeader(PoseFile pose)
    {
        if (!string.Equals(pose.Format, FormatIdentifier, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported pose format '{pose.Format}'.");
        }
        if (pose.Version is not LegacyVersion and not CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported pose version '{pose.Version}'.");
        }
        if (pose.Version >= CurrentVersion &&
            !string.Equals(pose.TranslationSpace, TranslationSpaceIdentifier, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported translation space '{pose.TranslationSpace}'.");
        }
        if (!string.Equals(pose.RotationSpace, RotationSpaceIdentifier, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported rotation space '{pose.RotationSpace}'.");
        }
        if (!string.Equals(pose.EulerOrder, EulerOrderIdentifier, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported Euler order '{pose.EulerOrder}'.");
        }
    }

    private static PoseQuaternion NormalizeQuaternion(
        double x,
        double y,
        double z,
        double w,
        string boneName)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) || !double.IsFinite(w))
        {
            throw new InvalidDataException($"Bone '{boneName}' contains a non-finite quaternion.");
        }

        var lengthSquared = x * x + y * y + z * z + w * w;
        if (!double.IsFinite(lengthSquared) || lengthSquared < MinimumQuaternionLengthSquared)
        {
            throw new InvalidDataException($"Bone '{boneName}' contains a zero or extremely small quaternion.");
        }

        var inverseLength = 1.0 / Math.Sqrt(lengthSquared);
        return new PoseQuaternion
        {
            X = x * inverseLength,
            Y = y * inverseLength,
            Z = z * inverseLength,
            W = w * inverseLength
        };
    }

    private static PoseVector3 ReadFiniteVector3(JsonElement element, string propertyName)
    {
        return ValidateVector3(new PoseVector3
        {
            X = ReadRequiredDouble(element, "x", propertyName),
            Y = ReadRequiredDouble(element, "y", propertyName),
            Z = ReadRequiredDouble(element, "z", propertyName)
        }, propertyName);
    }

    private static PoseVector3 ValidateVector3(PoseVector3 vector, string propertyName)
    {
        if (!double.IsFinite(vector.X) ||
            !double.IsFinite(vector.Y) ||
            !double.IsFinite(vector.Z))
        {
            throw new InvalidDataException(
                $"The pose file property '{propertyName}' contains a non-finite vector.");
        }
        return new PoseVector3 { X = vector.X, Y = vector.Y, Z = vector.Z };
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"The pose file property '{propertyName}' is missing or invalid.");
        }
        return property.GetString()!;
    }

    private static int ReadRequiredInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var value))
        {
            throw new InvalidDataException($"The pose file property '{propertyName}' is missing or invalid.");
        }
        return value;
    }

    private static double ReadRequiredDouble(JsonElement element, string propertyName, string boneName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetDouble(out var value))
        {
            throw new InvalidDataException(
                $"Bone '{boneName}' quaternion component '{propertyName}' is missing or invalid.");
        }
        return value;
    }
}
