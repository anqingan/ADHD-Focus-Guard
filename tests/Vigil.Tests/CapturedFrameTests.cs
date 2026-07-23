using Vigil.Core;

namespace Vigil.Tests;

public sealed class CapturedFrameTests
{
    [Fact]
    public void Dispose_ClearsImageAndHashBuffers()
    {
        byte[] jpeg = [1, 2, 3, 4];
        byte[] hash = [5, 6, 7, 8];
        var frame = new CapturedFrame(jpeg, hash);

        frame.Dispose();

        Assert.All(jpeg, value => Assert.Equal(0, value));
        Assert.All(hash, value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => _ = frame.Jpeg);
    }
}
