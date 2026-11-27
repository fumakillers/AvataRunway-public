using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AvataRunway.Core;

public sealed class GlbDocument
{
    public const uint JsonChunkType = 0x4E4F534A;
    public const uint BinChunkType = 0x004E4942;

    private const uint GlbMagic = 0x46546C67;
    private const uint GlbVersion = 2;

    private readonly List<GlbChunk> chunks;
    private byte[] binaryBuffer;

    private GlbDocument(JsonObject root, byte[] binaryBuffer, int binaryLogicalLength, List<GlbChunk> chunks)
    {
        Root = root;
        this.binaryBuffer = binaryBuffer;
        BinaryLogicalLength = binaryLogicalLength;
        this.chunks = chunks;
    }

    public JsonObject Root { get; }

    public byte[] BinaryBuffer => binaryBuffer;

    public int BinaryLogicalLength { get; private set; }

    public IReadOnlyList<GlbChunk> Chunks => chunks;

    public static GlbDocument Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20)
        {
            throw new InvalidVrmDataException("The GLB is too short.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != GlbMagic)
        {
            throw new InvalidVrmDataException("The GLB magic is invalid.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != GlbVersion)
        {
            throw new InvalidVrmDataException("Only GLB version 2 is supported.");
        }

        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (declaredLength != bytes.Length)
        {
            throw new InvalidVrmDataException("The GLB declared length does not match the file length.");
        }

        var chunks = new List<GlbChunk>();
        JsonObject? root = null;
        byte[]? binaryChunk = null;
        var offset = 12;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 8)
            {
                throw new InvalidVrmDataException("The GLB chunk header is truncated.");
            }

            var chunkLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]));
            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
            offset += 8;
            if (chunkLength < 0 || chunkLength % 4 != 0 || chunkLength > bytes.Length - offset)
            {
                throw new InvalidVrmDataException("A GLB chunk has an invalid length or alignment.");
            }

            var data = bytes.Slice(offset, chunkLength).ToArray();
            offset += chunkLength;
            if (chunkType == JsonChunkType)
            {
                if (root is not null || chunks.Count != 0)
                {
                    throw new InvalidVrmDataException("The GLB must contain exactly one first JSON chunk.");
                }

                try
                {
                    var jsonText = Encoding.UTF8.GetString(data).TrimEnd(' ', '\0');
                    root = JsonNode.Parse(jsonText)?.AsObject()
                        ?? throw new InvalidVrmDataException("The GLB JSON root is not an object.");
                }
                catch (JsonException exception)
                {
                    throw new InvalidVrmDataException("The GLB JSON chunk is invalid.", exception);
                }
            }
            else if (chunkType == BinChunkType)
            {
                if (binaryChunk is not null)
                {
                    throw new InvalidVrmDataException("Multiple BIN chunks are not supported.");
                }

                binaryChunk = data;
            }

            chunks.Add(new GlbChunk(chunkType, data));
        }

        if (root is null)
        {
            throw new InvalidVrmDataException("The GLB JSON chunk is missing.");
        }

        var logicalLength = GetDeclaredBufferLength(root);
        binaryChunk ??= [];
        if (logicalLength > binaryChunk.Length || binaryChunk.Length - logicalLength > 3)
        {
            throw new InvalidVrmDataException("buffers[0].byteLength is inconsistent with the BIN chunk length.");
        }

        return new GlbDocument(root, binaryChunk[..logicalLength], logicalLength, chunks);
    }

    public GlbDocument DeepClone()
    {
        var root = JsonNode.Parse(Root.ToJsonString())!.AsObject();
        return new GlbDocument(
            root,
            binaryBuffer.ToArray(),
            BinaryLogicalLength,
            chunks.Select(chunk => new GlbChunk(chunk.Type, chunk.Data.ToArray())).ToList());
    }

    public int AppendBinary(ReadOnlySpan<byte> data, int alignment = 4)
    {
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment));
        }

        var alignedOffset = checked((BinaryLogicalLength + alignment - 1) & -alignment);
        var newLength = checked(alignedOffset + data.Length);
        Array.Resize(ref binaryBuffer, newLength);
        data.CopyTo(binaryBuffer.AsSpan(alignedOffset));
        BinaryLogicalLength = newLength;
        SetDeclaredBufferLength(Root, newLength);
        return alignedOffset;
    }

    public byte[] Write()
    {
        SetDeclaredBufferLength(Root, BinaryLogicalLength);
        var json = Encoding.UTF8.GetBytes(Root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        var paddedJsonLength = Align4(json.Length);
        var paddedBinLength = Align4(BinaryLogicalLength);

        var emittedChunks = new List<(uint Type, byte[] Data, int LogicalLength)>();
        foreach (var chunk in chunks)
        {
            if (chunk.Type == JsonChunkType)
            {
                emittedChunks.Add((JsonChunkType, json, json.Length));
            }
            else if (chunk.Type == BinChunkType)
            {
                emittedChunks.Add((BinChunkType, binaryBuffer, BinaryLogicalLength));
            }
            else
            {
                emittedChunks.Add((chunk.Type, chunk.Data, chunk.Data.Length));
            }
        }

        if (emittedChunks.All(chunk => chunk.Type != BinChunkType) && BinaryLogicalLength > 0)
        {
            emittedChunks.Insert(1, (BinChunkType, binaryBuffer, BinaryLogicalLength));
        }

        var totalLength = 12;
        foreach (var chunk in emittedChunks)
        {
            var paddedLength = chunk.Type switch
            {
                JsonChunkType => paddedJsonLength,
                BinChunkType => paddedBinLength,
                _ => Align4(chunk.LogicalLength)
            };
            totalLength = checked(totalLength + 8 + paddedLength);
        }

        var result = new byte[totalLength];
        BinaryPrimitives.WriteUInt32LittleEndian(result, GlbMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), GlbVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), checked((uint)totalLength));

        var outputOffset = 12;
        foreach (var chunk in emittedChunks)
        {
            var paddedLength = Align4(chunk.LogicalLength);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(outputOffset), checked((uint)paddedLength));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(outputOffset + 4), chunk.Type);
            outputOffset += 8;
            chunk.Data.AsSpan(0, chunk.LogicalLength).CopyTo(result.AsSpan(outputOffset));
            if (chunk.Type == JsonChunkType)
            {
                result.AsSpan(outputOffset + chunk.LogicalLength, paddedLength - chunk.LogicalLength).Fill(0x20);
            }
            outputOffset += paddedLength;
        }

        return result;
    }

    private static int GetDeclaredBufferLength(JsonObject root)
    {
        var buffers = root["buffers"]?.AsArray()
            ?? throw new InvalidVrmDataException("The GLB buffers array is missing.");
        if (buffers.Count != 1 || buffers[0] is not JsonObject buffer || buffer["uri"] is not null)
        {
            throw new InvalidVrmDataException("Only a single embedded GLB buffer is supported.");
        }

        var length = buffer["byteLength"]?.GetValue<int>()
            ?? throw new InvalidVrmDataException("buffers[0].byteLength is missing.");
        if (length < 0)
        {
            throw new InvalidVrmDataException("buffers[0].byteLength is negative.");
        }
        return length;
    }

    private static void SetDeclaredBufferLength(JsonObject root, int length)
    {
        var buffer = root["buffers"]?.AsArray()[0]?.AsObject()
            ?? throw new InvalidVrmDataException("The GLB buffer definition is missing.");
        buffer["byteLength"] = length;
    }

    private static int Align4(int value) => checked((value + 3) & ~3);
}

public sealed record GlbChunk(uint Type, byte[] Data);
