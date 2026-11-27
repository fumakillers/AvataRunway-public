using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace AvataRunway.Core.Tests;

internal static class SyntheticVrmFixture
{
    internal const uint UnknownChunkType = 0x12345678;

    public static byte[] CreateVrm1(
        bool includeCustomExpression,
        bool changePositions = false,
        bool unsupportedMaterialBind = false,
        bool changeUv = false,
        bool includeMorphNormal = false,
        bool includeMorphTangent = false,
        bool omitBaseNormal = false,
        bool omitBaseTangent = false,
        bool includeMorphWeightAnimation = false)
    {
        using var binary = new MemoryStream();
        var bufferViews = new JsonArray();
        var accessors = new JsonArray();

        var positions = changePositions
            ? new float[] { 0.02f, 0f, 0f, 1.02f, 0f, 0f, 0.02f, 1f, 0f }
            : new float[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f };
        var positionAccessor = AddFloatAccessor(binary, bufferViews, accessors, positions, "VEC3", includeMinMax: true);
        var uvAccessor = AddFloatAccessor(
            binary,
            bufferViews,
            accessors,
            changeUv ? [0.1f, 0f, 1f, 0f, 0f, 1f] : [0f, 0f, 1f, 0f, 0f, 1f],
            "VEC2");
        var jointsAccessor = AddUnsignedShortAccessor(binary, bufferViews, accessors, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], "VEC4");
        var weightsAccessor = AddFloatAccessor(binary, bufferViews, accessors, [1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f], "VEC4");
        var indexAccessor = AddUnsignedShortAccessor(binary, bufferViews, accessors, [0, 1, 2], "SCALAR");
        var normalAccessor = omitBaseNormal
            ? (int?)null
            : AddFloatAccessor(binary, bufferViews, accessors, [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f], "VEC3");
        var tangentAccessor = omitBaseTangent
            ? (int?)null
            : AddFloatAccessor(binary, bufferViews, accessors, [1f, 0f, 0f, 1f, 1f, 0f, 0f, 1f, 1f, 0f, 0f, 1f], "VEC4");

        var primitive = new JsonObject
        {
            ["attributes"] = new JsonObject
            {
                ["POSITION"] = positionAccessor,
                ["TEXCOORD_0"] = uvAccessor,
                ["JOINTS_0"] = jointsAccessor,
                ["WEIGHTS_0"] = weightsAccessor
            },
            ["indices"] = indexAccessor,
            ["mode"] = 4
        };
        if (normalAccessor is not null)
        {
            primitive["attributes"]!["NORMAL"] = normalAccessor.Value;
        }
        if (tangentAccessor is not null)
        {
            primitive["attributes"]!["TANGENT"] = tangentAccessor.Value;
        }
        var secondPrimitive = JsonNode.Parse(primitive.ToJsonString())!.AsObject();
        var mesh = new JsonObject
        {
            ["primitives"] = new JsonArray(primitive, secondPrimitive),
            ["weights"] = new JsonArray(),
            ["extras"] = new JsonObject { ["targetNames"] = new JsonArray() }
        };
        var node = new JsonObject { ["mesh"] = 0, ["skin"] = 0, ["weights"] = new JsonArray() };

        var custom = new JsonObject();
        if (includeCustomExpression)
        {
            var morphValues = new float[] { 0f, 0.01f, 0f, 0f, 0.02f, 0f, 0f, 0.01f, 0f };
            var morphAccessor = AddFloatAccessor(binary, bufferViews, accessors, morphValues, "VEC3", includeMinMax: true);
            var morphTarget = new JsonObject { ["POSITION"] = morphAccessor };
            if (includeMorphNormal)
            {
                morphTarget["NORMAL"] = AddFloatAccessor(binary, bufferViews, accessors, [0f, 0.01f, 0f, 0f, 0.01f, 0f, 0f, 0.01f, 0f], "VEC3");
            }
            if (includeMorphTangent)
            {
                morphTarget["TANGENT"] = AddFloatAccessor(binary, bufferViews, accessors, [0.01f, 0f, 0f, 0.01f, 0f, 0f, 0.01f, 0f, 0f], "VEC3");
            }
            primitive["targets"] = new JsonArray(morphTarget);
            secondPrimitive["targets"] = new JsonArray(JsonNode.Parse(morphTarget.ToJsonString())!.AsObject());
            mesh["weights"]!.AsArray().Add(0f);
            mesh["extras"]!["targetNames"]!.AsArray().Add("AvataRunwayTestMorph");
            node["weights"]!.AsArray().Add(0f);
            var expression = new JsonObject
            {
                ["isBinary"] = false,
                ["overrideBlink"] = "blend",
                ["overrideLookAt"] = "none",
                ["overrideMouth"] = "block",
                ["morphTargetBinds"] = new JsonArray(new JsonObject
                {
                    ["node"] = 0,
                    ["index"] = 0,
                    ["weight"] = 0.75f
                })
            };
            if (unsupportedMaterialBind)
            {
                expression["materialColorBinds"] = new JsonArray(new JsonObject
                {
                    ["material"] = 0,
                    ["type"] = "color",
                    ["targetValue"] = new JsonArray(1f, 1f, 1f, 1f)
                });
            }
            custom["AvataRunwayTest"] = expression;
        }

        var root = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "AvataRunway synthetic fixture" },
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = checked((int)binary.Length) }),
            ["bufferViews"] = bufferViews,
            ["accessors"] = accessors,
            ["meshes"] = new JsonArray(mesh),
            ["nodes"] = new JsonArray(node, new JsonObject { ["name"] = "RootBone" }),
            ["skins"] = new JsonArray(new JsonObject { ["joints"] = new JsonArray(1) }),
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0, 1) }),
            ["scene"] = 0,
            ["extensionsUsed"] = new JsonArray("VRMC_vrm", "VENDOR_unknown"),
            ["extensions"] = new JsonObject
            {
                ["VRMC_vrm"] = new JsonObject
                {
                    ["specVersion"] = "1.0",
                    ["meta"] = new JsonObject { ["name"] = "Synthetic" },
                    ["humanoid"] = new JsonObject(),
                    ["expressions"] = new JsonObject
                    {
                        ["preset"] = new JsonObject
                        {
                            ["happy"] = new JsonObject { ["isBinary"] = false }
                        },
                        ["custom"] = custom
                    }
                },
                ["VENDOR_unknown"] = new JsonObject { ["preserveMe"] = 42 }
            }
        };
        if (includeMorphWeightAnimation)
        {
            root["animations"] = new JsonArray(new JsonObject
            {
                ["channels"] = new JsonArray(new JsonObject
                {
                    ["sampler"] = 0,
                    ["target"] = new JsonObject { ["node"] = 0, ["path"] = "weights" }
                }),
                ["samplers"] = new JsonArray(new JsonObject { ["input"] = 0, ["output"] = 0 })
            });
        }
        return WriteGlb(root, binary.ToArray(), (UnknownChunkType, new byte[] { 1, 2, 3, 4 }));
    }

    public static byte[] CreateVrm0(
        bool includeUnknownClip,
        bool changePositions = false,
        bool includeMorphNormal = false,
        bool omitBaseNormal = false,
        bool includeMorphWeightAnimation = false,
        bool unsupportedMaterialBind = false,
        bool changeUv = false)
    {
        var document = GlbDocument.Parse(CreateVrm1(
            includeCustomExpression: includeUnknownClip,
            changePositions: changePositions,
            includeMorphNormal: includeMorphNormal,
            omitBaseNormal: omitBaseNormal,
            includeMorphWeightAnimation: includeMorphWeightAnimation,
            changeUv: changeUv));
        var groups = new JsonArray(new JsonObject
        {
            ["name"] = "Joy",
            ["presetName"] = "joy",
            ["isBinary"] = false,
            ["binds"] = new JsonArray(),
            ["materialValues"] = new JsonArray()
        });
        if (includeUnknownClip)
        {
            var unknown = new JsonObject
            {
                ["name"] = "AvataRunwayTest",
                ["presetName"] = "unknown",
                ["isBinary"] = false,
                ["binds"] = new JsonArray(new JsonObject
                {
                    ["mesh"] = 0,
                    ["index"] = 0,
                    ["weight"] = 75f
                }),
                ["materialValues"] = new JsonArray()
            };
            if (unsupportedMaterialBind)
            {
                unknown["materialValues"]!.AsArray().Add(new JsonObject
                {
                    ["materialName"] = "Face",
                    ["propertyName"] = "_Color",
                    ["targetValue"] = new JsonArray(1f, 1f, 1f, 1f)
                });
            }
            groups.Add(unknown);
        }
        var vendor = document.Root["extensions"]!["VENDOR_unknown"]!.DeepClone();
        document.Root["extensionsUsed"] = new JsonArray("VRM", "VENDOR_unknown");
        document.Root["extensions"] = new JsonObject
        {
            ["VRM"] = new JsonObject
            {
                ["specVersion"] = "0.0",
                ["meta"] = new JsonObject { ["title"] = "Synthetic VRM 0.x" },
                ["humanoid"] = new JsonObject(),
                ["blendShapeMaster"] = new JsonObject { ["blendShapeGroups"] = groups }
            },
            ["VENDOR_unknown"] = vendor
        };
        return document.Write();
    }

    private static int AddFloatAccessor(
        MemoryStream binary,
        JsonArray bufferViews,
        JsonArray accessors,
        float[] values,
        string type,
        bool includeMinMax = false)
    {
        Align4(binary);
        var offset = checked((int)binary.Position);
        Span<byte> bytes = stackalloc byte[4];
        foreach (var value in values)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(value));
            binary.Write(bytes);
        }
        var viewIndex = bufferViews.Count;
        bufferViews.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = values.Length * 4 });
        var componentCount = type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, _ => throw new ArgumentOutOfRangeException(nameof(type)) };
        var accessor = new JsonObject
        {
            ["bufferView"] = viewIndex,
            ["componentType"] = 5126,
            ["count"] = values.Length / componentCount,
            ["type"] = type
        };
        if (includeMinMax)
        {
            var min = Enumerable.Range(0, componentCount).Select(component => values.Where((_, index) => index % componentCount == component).Min()).ToArray();
            var max = Enumerable.Range(0, componentCount).Select(component => values.Where((_, index) => index % componentCount == component).Max()).ToArray();
            accessor["min"] = ToJsonArray(min);
            accessor["max"] = ToJsonArray(max);
        }
        var accessorIndex = accessors.Count;
        accessors.Add(accessor);
        return accessorIndex;
    }

    private static int AddUnsignedShortAccessor(
        MemoryStream binary,
        JsonArray bufferViews,
        JsonArray accessors,
        ushort[] values,
        string type)
    {
        Align4(binary);
        var offset = checked((int)binary.Position);
        Span<byte> bytes = stackalloc byte[2];
        foreach (var value in values)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            binary.Write(bytes);
        }
        var viewIndex = bufferViews.Count;
        bufferViews.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = values.Length * 2 });
        var componentCount = type switch { "SCALAR" => 1, "VEC4" => 4, _ => throw new ArgumentOutOfRangeException(nameof(type)) };
        var accessorIndex = accessors.Count;
        accessors.Add(new JsonObject
        {
            ["bufferView"] = viewIndex,
            ["componentType"] = 5123,
            ["count"] = values.Length / componentCount,
            ["type"] = type
        });
        return accessorIndex;
    }

    private static JsonArray ToJsonArray(IEnumerable<float> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
        {
            result.Add(value);
        }
        return result;
    }

    private static void Align4(MemoryStream stream)
    {
        while (stream.Position % 4 != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static byte[] WriteGlb(JsonObject root, byte[] binary, params (uint Type, byte[] Data)[] unknownChunks)
    {
        root["buffers"]![0]!["byteLength"] = binary.Length;
        var json = Encoding.UTF8.GetBytes(root.ToJsonString());
        var jsonLength = Align4(json.Length);
        var binLength = Align4(binary.Length);
        var total = 12 + 8 + jsonLength + 8 + binLength + unknownChunks.Sum(chunk => 8 + Align4(chunk.Data.Length));
        var result = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), checked((uint)total));
        var offset = 12;
        WriteChunk(result, ref offset, 0x4E4F534A, json, 0x20);
        WriteChunk(result, ref offset, 0x004E4942, binary, 0);
        foreach (var chunk in unknownChunks)
        {
            WriteChunk(result, ref offset, chunk.Type, chunk.Data, 0);
        }
        return result;
    }

    private static void WriteChunk(byte[] destination, ref int offset, uint type, byte[] data, byte padding)
    {
        var paddedLength = Align4(data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(offset), checked((uint)paddedLength));
        BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(offset + 4), type);
        offset += 8;
        data.CopyTo(destination, offset);
        destination.AsSpan(offset + data.Length, paddedLength - data.Length).Fill(padding);
        offset += paddedLength;
    }

    private static int Align4(int value) => (value + 3) & ~3;
}
