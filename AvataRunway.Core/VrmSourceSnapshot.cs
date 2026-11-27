using System.Security.Cryptography;

namespace AvataRunway.Core;

public sealed class VrmSourceSnapshot
{
    private readonly byte[] bytes;

    private VrmSourceSnapshot(byte[] bytes, string? sourcePath)
    {
        this.bytes = bytes;
        SourcePath = sourcePath;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public string? SourcePath { get; }

    public string Sha256 { get; }

    public int Length => bytes.Length;

    public static VrmSourceSnapshot Create(ReadOnlySpan<byte> sourceBytes, string? sourcePath = null)
    {
        var ownedBytes = sourceBytes.ToArray();
        _ = GlbDocument.Parse(ownedBytes);
        return new VrmSourceSnapshot(
            ownedBytes,
            string.IsNullOrWhiteSpace(sourcePath) ? null : Path.GetFullPath(sourcePath));
    }

    public Stream OpenRead() => new MemoryStream(bytes, writable: false);

    public byte[] CopyBytes() => bytes.ToArray();
}
