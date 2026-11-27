using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AvataRunway.Core;

public enum MeshCompatibilityGrade
{
    Incompatible = 0,
    CompatibleWithGeometryDifferences = 1,
    Exact = 2
}

public sealed record PrimitiveFingerprint(
    int VertexCount,
    int IndexCount,
    int Mode,
    string TopologySha256,
    string VertexIdentitySha256,
    string PositionSha256,
    bool HasStrongVertexIdentity);

public sealed class MeshFingerprintService
{
    private static readonly string[] IdentityPrefixes = ["TEXCOORD_", "JOINTS_", "WEIGHTS_"];

    public PrimitiveFingerprint Create(GlbDocument document, int meshIndex, int primitiveIndex)
    {
        var primitive = GetPrimitive(document.Root, meshIndex, primitiveIndex);
        var attributes = primitive["attributes"]?.AsObject()
            ?? throw new InvalidVrmDataException("A mesh primitive attributes object is missing.");
        var positionAccessor = attributes["POSITION"]?.GetValue<int>()
            ?? throw new InvalidVrmDataException("A mesh primitive POSITION accessor is missing.");
        var reader = new GltfAccessorReader(document);
        var positionInfo = reader.GetInfo(positionAccessor);
        if (positionInfo.Type != "VEC3")
        {
            throw new InvalidVrmDataException("A POSITION accessor must be VEC3.");
        }

        var indices = primitive["indices"] is JsonValue indexValue
            ? reader.ReadUnsignedIntegers(indexValue.GetValue<int>())
            : Enumerable.Range(0, positionInfo.Count).Select(value => checked((uint)value)).ToArray();
        var mode = primitive["mode"]?.GetValue<int>() ?? 4;
        var topologyHash = Hash(writer =>
        {
            writer.WriteString("avatarunway-topology-v1");
            writer.WriteInt32(mode);
            writer.WriteInt32(positionInfo.Count);
            writer.WriteInt32(indices.Length);
            writer.WriteUInt32Array(indices);
        });

        var identityNames = attributes
            .Where(pair => IdentityPrefixes.Any(prefix => pair.Key.StartsWith(prefix, StringComparison.Ordinal)))
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var hasUv = identityNames.Any(name => name.StartsWith("TEXCOORD_", StringComparison.Ordinal));
        var hasJoints = identityNames.Any(name => name.StartsWith("JOINTS_", StringComparison.Ordinal));
        var hasWeights = identityNames.Any(name => name.StartsWith("WEIGHTS_", StringComparison.Ordinal));
        var identityHash = Hash(writer =>
        {
            writer.WriteString("avatarunway-vertex-identity-v1");
            writer.WriteString(topologyHash);
            writer.WriteInt32(identityNames.Length);
            foreach (var name in identityNames)
            {
                writer.WriteString(name);
                var accessorIndex = attributes[name]!.GetValue<int>();
                var info = reader.GetInfo(accessorIndex);
                writer.WriteString(info.Type);
                writer.WriteInt32(info.Count);
                if (name.StartsWith("JOINTS_", StringComparison.Ordinal))
                {
                    writer.WriteUInt32Array(reader.ReadUnsignedIntegers(accessorIndex));
                }
                else
                {
                    writer.WriteFloatArray(reader.ReadFloat32(accessorIndex));
                }
            }
        });
        var positionHash = Hash(writer =>
        {
            writer.WriteString("avatarunway-position-v1");
            writer.WriteFloatArray(reader.ReadFloat32(positionAccessor));
        });

        return new PrimitiveFingerprint(
            positionInfo.Count,
            indices.Length,
            mode,
            topologyHash,
            identityHash,
            positionHash,
            hasUv && hasJoints && hasWeights);
    }

    public MeshCompatibilityGrade Compare(PrimitiveFingerprint source, PrimitiveFingerprint target)
    {
        if (source.VertexCount != target.VertexCount ||
            source.IndexCount != target.IndexCount ||
            source.Mode != target.Mode ||
            !string.Equals(source.TopologySha256, target.TopologySha256, StringComparison.Ordinal) ||
            !string.Equals(source.VertexIdentitySha256, target.VertexIdentitySha256, StringComparison.Ordinal))
        {
            return MeshCompatibilityGrade.Incompatible;
        }

        if (string.Equals(source.PositionSha256, target.PositionSha256, StringComparison.Ordinal))
        {
            return MeshCompatibilityGrade.Exact;
        }

        return source.HasStrongVertexIdentity && target.HasStrongVertexIdentity
            ? MeshCompatibilityGrade.CompatibleWithGeometryDifferences
            : MeshCompatibilityGrade.Incompatible;
    }

    internal static JsonObject GetPrimitive(JsonObject root, int meshIndex, int primitiveIndex)
    {
        var mesh = GltfAccessorReader.GetObject(root, "meshes", meshIndex);
        var primitives = mesh["primitives"]?.AsArray()
            ?? throw new InvalidVrmDataException("A mesh primitives array is missing.");
        if (primitiveIndex < 0 || primitiveIndex >= primitives.Count || primitives[primitiveIndex] is not JsonObject primitive)
        {
            throw new InvalidVrmDataException("A primitive index is out of range.");
        }
        return primitive;
    }

    private static string Hash(Action<CanonicalHashWriter> write)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        write(new CanonicalHashWriter(hash));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private sealed class CanonicalHashWriter(IncrementalHash hash)
    {
        private readonly byte[] scalarBuffer = new byte[4];

        public void WriteInt32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(scalarBuffer, value);
            hash.AppendData(scalarBuffer);
        }

        public void WriteString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteInt32(bytes.Length);
            hash.AppendData(bytes);
        }

        public void WriteUInt32Array(ReadOnlySpan<uint> values)
        {
            WriteInt32(values.Length);
            foreach (var value in values)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(scalarBuffer, value);
                hash.AppendData(scalarBuffer);
            }
        }

        public void WriteFloatArray(ReadOnlySpan<float> values)
        {
            WriteInt32(values.Length);
            foreach (var sourceValue in values)
            {
                var value = sourceValue == 0f ? 0f : sourceValue;
                if (!float.IsFinite(value))
                {
                    throw new InvalidVrmDataException("A fingerprint input contains NaN or Infinity.");
                }
                BinaryPrimitives.WriteInt32LittleEndian(scalarBuffer, BitConverter.SingleToInt32Bits(value));
                hash.AppendData(scalarBuffer);
            }
        }
    }
}
