using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
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

        TrimToTextRow(left, 0.70);
        TrimToTextRow(right, 0.70);

        // Для RapidOCR лучше нативный белый OSD на тёмном, без инверсии
        return (
            ToJpeg(left, options.DateStripWidthPx, options.MinOutputHeightPx),
            ToJpeg(right, options.GpsStripWidthPx, options.MinOutputHeightPx),
            w,
            h);
    }

    private static void TrimToTextRow(Image<Rgb24> strip, double keepBottomRatio)
    {
        keepBottomRatio = Math.Clamp(keepBottomRatio, 0.25, 1.0);
        var keepH = Math.Max(28, (int)(strip.Height * keepBottomRatio));
        if (keepH >= strip.Height) return;
        var y = strip.Height - keepH;
        strip.Mutate(x => x.Crop(new Rectangle(0, y, strip.Width, keepH)));
    }

    private static byte[] ToJpeg(Image<Rgb24> strip, int targetW, int targetH)
    {
        strip.Mutate(s =>
        {
            s.Resize(targetW, Math.Max(targetH, 64), KnownResamplers.Lanczos3);
            s.Contrast(1.15f);
        });

        using var ms = new MemoryStream();
        strip.SaveAsJpeg(ms, new JpegEncoder { Quality = 92 });
        return ms.ToArray();
    }
}
