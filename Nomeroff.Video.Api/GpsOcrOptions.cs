namespace Nomeroff.Video.Api;

public sealed class GpsOcrOptions
{
    public const string SectionName = "GpsOcr";

    public bool Enabled { get; set; } = true;

    /// <summary>Auto = Tesseract, при неудаче Python (EasyOCR). Tesseract | Python</summary>
    public string Engine { get; set; } = "Auto";

    public double CropBottomRatio { get; set; } = 0.2;

    public int MinCropHeightPx { get; set; } = 90;

    public double GpsRightWidthRatio { get; set; } = 0.5;

    public double DateTimeLeftWidthRatio { get; set; } = 0.5;

    public int MinOutputHeightPx { get; set; } = 180;

    public int GpsStripWidthPx { get; set; } = 1400;

    public int DateStripWidthPx { get; set; } = 1000;

    public string TessDataSubdir { get; set; } = "tessdata";

    public bool LogOcrTextOnMiss { get; set; } = true;
}
