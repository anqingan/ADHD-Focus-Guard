using Vigil.Core;

namespace Vigil.Tests;

public sealed class DHashTests
{
    [Fact]
    public void Distance_CountsDifferentBits()
    {
        var left = new byte[DHash.ByteLength];
        var right = new byte[DHash.ByteLength];
        right[0] = 0b1010_0001;

        Assert.Equal(3, DHash.Distance(left, right));
    }

    [Fact]
    public void Distance_ReturnsMaxForDifferentLengths()
    {
        Assert.Equal(int.MaxValue, DHash.Distance(new byte[31], new byte[32]));
    }

    [Fact]
    public void FromGrayscale_Produces256BitHash()
    {
        var pixels = new byte[17 * 16];
        for (var row = 0; row < 16; row++)
        for (var col = 0; col < 17; col++)
            pixels[row * 17 + col] = (byte)(255 - col);

        var hash = DHash.FromGrayscale17x16(pixels);

        Assert.Equal(32, hash.Length);
        Assert.All(hash, value => Assert.Equal(255, value));
    }
}
