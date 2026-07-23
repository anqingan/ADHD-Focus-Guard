using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Vigil.Core;

namespace Vigil.Infrastructure;

public sealed class GdiScreenCaptureService : IScreenCaptureService
{
    private const int MaxLongEdge = 1280;

    public Task<CapturedFrame> CapturePrimaryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
                     ?? throw new InvalidOperationException("找不到主显示器。");

        using var source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        using var scaled = Scale(source, MaxLongEdge);
        var jpeg = EncodeJpeg(scaled, 65L);
        var hash = ComputeDHash(scaled);
        return Task.FromResult(new CapturedFrame(jpeg, hash));
    }

    public static byte[] CreateSyntheticTestImage()
    {
        using var bitmap = new Bitmap(320, 120, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.RoyalBlue);
        using var font = new Font("Arial", 24, FontStyle.Bold);
        graphics.DrawString("VIGIL TEST 42", font, Brushes.White, new PointF(28, 40));
        return EncodeJpeg(bitmap, 85L);
    }

    private static Bitmap Scale(Bitmap source, int maxLongEdge)
    {
        var ratio = Math.Min(1.0, maxLongEdge / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * ratio));
        var height = Math.Max(1, (int)Math.Round(source.Height * ratio));
        var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(result);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, width, height);
        return result;
    }

    private static byte[] ComputeDHash(Bitmap source)
    {
        using var small = new Bitmap(17, 16, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(small))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, 0, 0, 17, 16);
        }

        var grayscale = new byte[17 * 16];
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 17; x++)
            {
                var color = small.GetPixel(x, y);
                grayscale[y * 17 + x] = (byte)Math.Clamp(
                    (int)Math.Round(color.R * 0.299 + color.G * 0.587 + color.B * 0.114),
                    0,
                    255);
            }
        }
        return DHash.FromGrayscale17x16(grayscale);
    }

    private static byte[] EncodeJpeg(Image image, long quality)
    {
        using var stream = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().First(item => item.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        image.Save(stream, codec, parameters);
        return stream.ToArray();
    }
}
