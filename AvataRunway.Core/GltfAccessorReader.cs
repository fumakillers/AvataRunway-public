using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace AvataRunway.Core;

public sealed class GltfAccessorReader
{
    private readonly GlbDocument document;

    public GltfAccessorReader(GlbDocument document)
    {
        this.document = document;
    }

    public AccessorInfo GetInfo(int accessorIndex)
    {
        var accessor = GetObject(document.Root, "accessors", accessorIndex);
        var type = accessor["type"]?.GetValue<string>()
            ?? throw new InvalidVrmDataException("An accessor type is missing.");
        var componentCount = GetComponentCount(type);
        var componentType = accessor["componentType"]?.GetValue<int>()
            ?? throw new InvalidVrmDataException("An accessor componentType is missing.");
        var count = accessor["count"]?.GetValue<int>()
            ?? throw new InvalidVrmDataException("An accessor count is missing.");
        if (count < 0)
        {
            throw new InvalidVrmDataException("An accessor count is negative.");
        }

        return new AccessorInfo(type, componentType, componentCount, count, accessor["normalized"]?.GetValue<bool>() ?? false);
    }

    public float[] ReadFloat32(int accessorIndex)
    {
        var accessor = GetObject(document.Root, "accessors", accessorIndex);
        var info = GetInfo(accessorIndex);
        var values = new float[checked(info.Count * info.ComponentCount)];
        ReadBase(accessor, info, (destinationIndex, source) =>
        {
            values[destinationIndex] = ReadFloatComponent(source, info.ComponentType, info.Normalized);
        });
        ApplySparse(accessor, info, (destinationIndex, source) =>
        {
            values[destinationIndex] = ReadFloatComponent(source, info.ComponentType, info.Normalized);
        });

        if (values.Any(value => !float.IsFinite(value)))
        {
            throw new InvalidVrmDataException("An accessor contains NaN or Infinity.");
        }

        return values;
    }

    public uint[] ReadUnsignedIntegers(int accessorIndex)
    {
        var accessor = GetObject(document.Root, "accessors", accessorIndex);
        var info = GetInfo(accessorIndex);
        if (info.ComponentType is not (5121 or 5123 or 5125) || info.Normalized)
        {
            throw new InvalidVrmDataException("The accessor is not an unsigned integer accessor.");
        }

        var values = new uint[checked(info.Count * info.ComponentCount)];
        ReadBase(accessor, info, (destinationIndex, source) => values[destinationIndex] = ReadUnsigned(source, info.ComponentType));
        ApplySparse(accessor, info, (destinationIndex, source) => values[destinationIndex] = ReadUnsigned(source, info.ComponentType));
        return values;
    }

    private void ReadBase(JsonObject accessor, AccessorInfo info, Action<int, ReadOnlySpan<byte>> assign)
    {
        if (accessor["bufferView"] is null)
        {
            return;
        }

        var bufferViewIndex = accessor["bufferView"]!.GetValue<int>();
        var view = GetObject(document.Root, "bufferViews", bufferViewIndex);
        ValidateEmbeddedBuffer(view);
        var componentSize = GetComponentSize(info.ComponentType);
        var elementSize = checked(componentSize * info.ComponentCount);
        var stride = view["byteStride"]?.GetValue<int>() ?? elementSize;
        if (stride < elementSize || stride % componentSize != 0)
        {
            throw new InvalidVrmDataException("A bufferView byteStride is invalid.");
        }

        var viewOffset = view["byteOffset"]?.GetValue<int>() ?? 0;
        var viewLength = view["byteLength"]?.GetValue<int>()
            ?? throw new InvalidVrmDataException("A bufferView byteLength is missing.");
        var accessorOffset = accessor["byteOffset"]?.GetValue<int>() ?? 0;
        ValidateRange(viewOffset, viewLength, document.BinaryLogicalLength, "bufferView");
        var required = info.Count == 0 ? 0 : checked((info.Count - 1) * stride + elementSize);
        ValidateRange(accessorOffset, required, viewLength, "accessor");

        for (var element = 0; element < info.Count; element++)
        {
            var elementOffset = checked(viewOffset + accessorOffset + element * stride);
            for (var component = 0; component < info.ComponentCount; component++)
            {
                var sourceOffset = checked(elementOffset + component * componentSize);
                assign(element * info.ComponentCount + component, document.BinaryBuffer.AsSpan(sourceOffset, componentSize));
            }
        }
    }

    private void ApplySparse(JsonObject accessor, AccessorInfo info, Action<int, ReadOnlySpan<byte>> assign)
    {
        if (accessor["sparse"] is not JsonObject sparse)
        {
            return;
        }

        var sparseCount = sparse["count"]?.GetValue<int>()
            ?? throw new InvalidVrmDataException("A sparse accessor count is missing.");
        if (sparseCount < 0 || sparseCount > info.Count)
        {
            throw new InvalidVrmDataException("A sparse accessor count is invalid.");
        }

        var indices = sparse["indices"]?.AsObject()
            ?? throw new InvalidVrmDataException("Sparse accessor indices are missing.");
        var values = sparse["values"]?.AsObject()
            ?? throw new InvalidVrmDataException("Sparse accessor values are missing.");
        var indexComponentType = indices["componentType"]?.GetValue<int>()
            ?? throw new InvalidVrmDataException("Sparse index componentType is missing.");
        if (indexComponentType is not (5121 or 5123 or 5125))
        {
            throw new InvalidVrmDataException("Sparse indices have an invalid componentType.");
        }

        var indexBytes = ReadBufferViewSlice(
            indices["bufferView"]?.GetValue<int>() ?? throw new InvalidVrmDataException("Sparse index bufferView is missing."),
            indices["byteOffset"]?.GetValue<int>() ?? 0,
            checked(sparseCount * GetComponentSize(indexComponentType)));
        var valueElementSize = checked(GetComponentSize(info.ComponentType) * info.ComponentCount);
        var valueBytes = ReadBufferViewSlice(
            values["bufferView"]?.GetValue<int>() ?? throw new InvalidVrmDataException("Sparse value bufferView is missing."),
            values["byteOffset"]?.GetValue<int>() ?? 0,
            checked(sparseCount * valueElementSize));

        var previousIndex = -1;
        for (var sparseElement = 0; sparseElement < sparseCount; sparseElement++)
        {
            var indexSize = GetComponentSize(indexComponentType);
            var destinationElement = checked((int)ReadUnsigned(indexBytes.Slice(sparseElement * indexSize, indexSize), indexComponentType));
            if (destinationElement <= previousIndex || destinationElement >= info.Count)
            {
                throw new InvalidVrmDataException("Sparse indices must be strictly increasing and in range.");
            }
            previousIndex = destinationElement;

            var componentSize = GetComponentSize(info.ComponentType);
            for (var component = 0; component < info.ComponentCount; component++)
            {
                var sourceOffset = checked(sparseElement * valueElementSize + component * componentSize);
                assign(destinationElement * info.ComponentCount + component, valueBytes.Slice(sourceOffset, componentSize));
            }
        }
    }

    private ReadOnlySpan<byte> ReadBufferViewSlice(int bufferViewIndex, int relativeOffset, int length)
    {
        var view = GetObject(document.Root, "bufferViews", bufferViewIndex);
        ValidateEmbeddedBuffer(view);
        if (view["byteStride"] is not null)
        {
            throw new InvalidVrmDataException("Sparse bufferViews must not define byteStride.");
        }
        var viewOffset = view["byteOffset"]?.GetValue<int>() ?? 0;
        var viewLength = view["byteLength"]?.GetValue<int>()
            ?? throw new InvalidVrmDataException("A bufferView byteLength is missing.");
        ValidateRange(viewOffset, viewLength, document.BinaryLogicalLength, "bufferView");
        ValidateRange(relativeOffset, length, viewLength, "bufferView slice");
        return document.BinaryBuffer.AsSpan(checked(viewOffset + relativeOffset), length);
    }

    private static void ValidateEmbeddedBuffer(JsonObject view)
    {
        if ((view["buffer"]?.GetValue<int>() ?? -1) != 0)
        {
            throw new InvalidVrmDataException("Only bufferViews in embedded buffer 0 are supported.");
        }
    }

    internal static JsonObject GetObject(JsonObject root, string arrayName, int index)
    {
        var array = root[arrayName]?.AsArray()
            ?? throw new InvalidVrmDataException($"The {arrayName} array is missing.");
        if (index < 0 || index >= array.Count || array[index] is not JsonObject item)
        {
            throw new InvalidVrmDataException($"A {arrayName} index is out of range.");
        }
        return item;
    }

    internal static int GetComponentCount(string type) => type switch
    {
        "SCALAR" => 1,
        "VEC2" => 2,
        "VEC3" => 3,
        "VEC4" => 4,
        "MAT2" => 4,
        "MAT3" => 9,
        "MAT4" => 16,
        _ => throw new InvalidVrmDataException($"Unsupported accessor type: {type}")
    };

    internal static int GetComponentSize(int componentType) => componentType switch
    {
        5120 or 5121 => 1,
        5122 or 5123 => 2,
        5125 or 5126 => 4,
        _ => throw new InvalidVrmDataException($"Unsupported accessor componentType: {componentType}")
    };

    private static float ReadFloatComponent(ReadOnlySpan<byte> source, int componentType, bool normalized)
    {
        return componentType switch
        {
            5120 => normalized ? Math.Max((sbyte)source[0] / 127f, -1f) : (sbyte)source[0],
            5121 => normalized ? source[0] / 255f : source[0],
            5122 => normalized ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(source) / 32767f, -1f) : BinaryPrimitives.ReadInt16LittleEndian(source),
            5123 => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(source) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(source),
            5125 when !normalized => BinaryPrimitives.ReadUInt32LittleEndian(source),
            5126 when !normalized => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source)),
            _ => throw new InvalidVrmDataException("The accessor component normalization is invalid.")
        };
    }

    private static uint ReadUnsigned(ReadOnlySpan<byte> source, int componentType) => componentType switch
    {
        5121 => source[0],
        5123 => BinaryPrimitives.ReadUInt16LittleEndian(source),
        5125 => BinaryPrimitives.ReadUInt32LittleEndian(source),
        _ => throw new InvalidVrmDataException("An unsigned accessor has an invalid componentType.")
    };

    private static void ValidateRange(int offset, int length, int containingLength, string label)
    {
        if (offset < 0 || length < 0 || offset > containingLength || length > containingLength - offset)
        {
            throw new InvalidVrmDataException($"A {label} range is out of bounds.");
        }
    }
}

public sealed record AccessorInfo(string Type, int ComponentType, int ComponentCount, int Count, bool Normalized);
