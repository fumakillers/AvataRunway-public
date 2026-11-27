using Xunit;

namespace AvataRunway.Core.Tests;

public sealed class VrmSourceSnapshotTests
{
    [Fact]
    public void Create_OwnsValidatedBytesWithoutDependingOnSourcePathOrCallerBuffer()
    {
        var callerBytes = SyntheticVrmFixture.CreateVrm1(includeCustomExpression: false);
        var expectedFirstByte = callerBytes[0];
        var snapshot = VrmSourceSnapshot.Create(callerBytes);

        callerBytes[0] ^= 0xff;
        var snapshotBytes = snapshot.CopyBytes();

        Assert.Null(snapshot.SourcePath);
        Assert.Equal(expectedFirstByte, snapshotBytes[0]);
        Assert.Equal(snapshot.Length, snapshotBytes.Length);
        Assert.NotEmpty(snapshot.Sha256);
        Assert.False(snapshot.OpenRead().CanWrite);
    }
}
