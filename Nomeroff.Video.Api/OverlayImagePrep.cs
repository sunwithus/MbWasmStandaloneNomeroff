using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Nomeroff.Video.Api;

/// <summary>Вырезка нижней полосы: слева дата/время, справа GPS+скорость.</summary>
internal static class OverlayImagePrep
{
    public static (byte[] LeftPng, byte[] RightPng, int FrameWidth, int FrameHeight) CreateStrips(
        string imagePath, GpsOcrOptions options)
    {
        using var image = Image.Load<Rgb24>(imagePath);
        if (image.Width < 1280)
        {
            var scale = 1280.0 / image.Width;
            image.Mutate(x => x.Resize((int)(image.Width * scale), (int)(image.Height * scale)));
        }

        var w = image.Width;
        var h = image.Height;
        var cropH = Math.Min(h, Math.Max(options.MinCropHeightPx, (int)(h * options.CropBottomRatio)));
        var y = h - cropH;

        var leftW = Math.Max(100, (int)(w * options.DateTimeLeftWidthRatio));
        var rightW = Math.Max(100, (int)(w * options.GpsRightWidthRatio));
        var rightX = w - rightW;

        using var left = image.Clone(ctx => ctx.Crop(new Rectangle(0, y, leftW, cropH)));
        using var right = image.Clone(ctx => ctx.Crop(new Rectangle(rightX, y, rightW, cropH)));

        return (
            ToPng(left, options.DateStripWidthPx, options.MinOutputHeightPx, sharpen: false),
            ToPng(right, options.GpsStripWidthPx, options.MinOutputHeightPx, sharpen: true),
            w,
            h);
    }

    private static byte[] ToPng(Image<Rgb24> strip, int targetW, int targetH, bool sharpen)
    {
        strip.Mutate(s =>
        {
            s.Resize(targetW, targetH);
            s.Grayscale();
            s.Contrast(1.4f);
            if (sharpen)
                s.GaussianSharpen();
        });

        using var ms = new MemoryStream();
        strip.SaveAsPng(ms);
        return ms.ToArray();
    }
}
