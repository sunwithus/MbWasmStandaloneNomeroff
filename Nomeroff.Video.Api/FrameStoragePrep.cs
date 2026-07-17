using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Nomeroff.Video.Api;

/// <summary>Сжатие кадра перед отдачей в UI/БД (JPEG, опционально уменьшение ширины).</summary>
internal static class FrameStoragePrep
{
    /// <param name="maxWidth">0 = не менять размер</param>
    /// <param name="jpegQuality">60–92</param>
    public static byte[] CompressJpeg(byte[] jpegBytes, int maxWidth = 1280, int jpegQuality = 72)
    {
        if (jpegBytes.Length == 0) return jpegBytes;
        jpegQuality = Math.Clamp(jpegQuality, 50, 95);

        using var image = Image.Load(jpegBytes);
        if (maxWidth > 0 && image.Width > maxWidth)
        {
            var h = (int)Math.Round(image.Height * (maxWidth / (double)image.Width));
            image.Mutate(x => x.Resize(maxWidth, h));
        }

        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new JpegEncoder { Quality = jpegQuality });
        return ms.ToArray();
    }
}
