using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Nomeroff.Video.Api;

/// <summary>
/// Подготовка кадра регистратора для OCR номера:
/// убрать OSD/капот и при необходимости увеличить зону дороги (мелкие номера вдалеке).
/// </summary>
public static class PlateFramePrep
{
    private static readonly JpegEncoder Jpeg90 = new() { Quality = 90 };

    /// <summary>Вырезать bbox [x1,y1,x2,y2] с небольшим padding; null если bbox пустой.</summary>
    public static byte[]? CropByBbox(byte[] jpegBytes, int[]? bbox, int padPx = 8)
    {
        if (jpegBytes.Length == 0 || bbox == null || bbox.Length < 4)
            return null;
        var x1 = Math.Min(bbox[0], bbox[2]);
        var y1 = Math.Min(bbox[1], bbox[3]);
        var x2 = Math.Max(bbox[0], bbox[2]);
        var y2 = Math.Max(bbox[1], bbox[3]);
        if (x2 - x1 < 8 || y2 - y1 < 8)
            return null;

        using var image = Image.Load<Rgb24>(jpegBytes);
        x1 = Math.Clamp(x1 - padPx, 0, image.Width - 1);
        y1 = Math.Clamp(y1 - padPx, 0, image.Height - 1);
        x2 = Math.Clamp(x2 + padPx, x1 + 1, image.Width);
        y2 = Math.Clamp(y2 + padPx, y1 + 1, image.Height);
        var w = x2 - x1;
        var h = y2 - y1;
        if (w < 8 || h < 8)
            return null;

        image.Mutate(x => x.Crop(new Rectangle(x1, y1, w, h)));
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, Jpeg90);
        return ms.ToArray();
    }

    public static byte[] CropBottom(byte[] jpegBytes, double bottomRatio)
    {
        if (bottomRatio <= 0.001 || jpegBytes.Length == 0)
            return jpegBytes;

        using var image = Image.Load<Rgb24>(jpegBytes);
        var cut = Math.Min(image.Height - 40, Math.Max(0, (int)(image.Height * bottomRatio)));
        if (cut <= 0)
            return jpegBytes;

        var keepH = image.Height - cut;
        image.Mutate(x => x.Crop(new Rectangle(0, 0, image.Width, keepH)));

        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, Jpeg90);
        return ms.ToArray();
    }

    /// <summary>
    /// Центрально-нижняя зона дороги (без капота/OSD), увеличенная ~1.5× —
    /// для мелких номеров в кадре регистратора 1920×1080.
    /// </summary>
    public static byte[] RoadRoiUpscaled(
        byte[] jpegBytes,
        double topRatio = 0.22,
        double bottomCropRatio = 0.14,
        double sideCropRatio = 0.12,
        double scale = 1.5)
    {
        if (jpegBytes.Length == 0)
            return jpegBytes;

        using var image = Image.Load<Rgb24>(jpegBytes);
        var w = image.Width;
        var h = image.Height;

        var y0 = Math.Clamp((int)(h * topRatio), 0, h - 80);
        var y1 = Math.Clamp(h - (int)(h * bottomCropRatio), y0 + 40, h);
        var x0 = Math.Clamp((int)(w * sideCropRatio), 0, w / 2);
        var x1 = Math.Clamp(w - (int)(w * sideCropRatio), x0 + 40, w);
        var rw = x1 - x0;
        var rh = y1 - y0;
        if (rw < 80 || rh < 80)
            return jpegBytes;

        image.Mutate(x =>
        {
            x.Crop(new Rectangle(x0, y0, rw, rh));
            if (scale > 1.01)
            {
                var nw = Math.Max(1, (int)(rw * scale));
                var nh = Math.Max(1, (int)(rh * scale));
                x.Resize(nw, nh);
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, Jpeg90);
        return ms.ToArray();
    }

    /// <summary>Лёгкий контраст для тёмных/военных номеров (после ROI или crop).</summary>
    public static byte[] ContrastBoost(byte[] jpegBytes, float contrast = 1.25f)
    {
        if (jpegBytes.Length == 0) return jpegBytes;
        using var image = Image.Load<Rgb24>(jpegBytes);
        image.Mutate(x => x.Contrast(contrast).Brightness(1.05f));
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, Jpeg90);
        return ms.ToArray();
    }

    /// <summary>Негатив — военные номера (белый на чёрном / чёрный на белом).</summary>
    public static byte[] Invert(byte[] jpegBytes)
    {
        if (jpegBytes.Length == 0) return jpegBytes;
        using var image = Image.Load<Rgb24>(jpegBytes);
        image.Mutate(x => x.Invert());
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, Jpeg90);
        return ms.ToArray();
    }
}
