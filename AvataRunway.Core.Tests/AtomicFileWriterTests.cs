using Xunit;

namespace AvataRunway.Core.Tests;

public sealed class AtomicFileWriterTests
{
    [Fact]
    public async Task WriteNewAsync_CreatesNewFileAndRejectsEveryExistingPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"AvataRunway-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "new.vrm");
            var writer = new AtomicFileWriter();
            await writer.WriteNewAsync(path, new byte[] { 1, 2, 3 });
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));

            await Assert.ThrowsAsync<IOException>(() => writer.WriteNewAsync(path, new byte[] { 9 }));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
