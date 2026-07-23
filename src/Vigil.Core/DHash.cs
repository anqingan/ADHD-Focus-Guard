using System.Numerics;

namespace Vigil.Core;

public static class DHash
{
    public const int ByteLength = 32;

    public static int Distance(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return int.MaxValue;
        }

        var distance = 0;
        for (var i = 0; i < left.Length; i++)
        {
            distance += BitOperations.PopCount((uint)(left[i] ^ right[i]));
        }
        return distance;
    }

    public static byte[] FromGrayscale17x16(ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length != 17 * 16)
        {
            throw new ArgumentException("dHash requires exactly 17x16 grayscale pixels.", nameof(pixels));
        }

        var hash = new byte[ByteLength];
        for (var row = 0; row < 16; row++)
        {
            for (var col = 0; col < 16; col++)
            {
                if (pixels[row * 17 + col] <= pixels[row * 17 + col + 1])
                {
                    continue;
                }
                var bit = row * 16 + col;
                hash[bit / 8] |= (byte)(1 << (7 - bit % 8));
            }
        }
        return hash;
    }
}
