namespace MbWebApp.Options;

/// <summary>
/// Единый серверный конфиг (appsettings.json + переменные окружения).
/// Примеры env: MaxVideoFrames=500, PlateMinConfidence=0.85,
/// NomeroffApiBaseUrl=http://127.0.0.1:8000, FolderWatch__WatchFolder=D:\REG_VIDEO
/// </summary>
public sealed class NomeroffAppOptions
{
    public const string SectionVideo = "Video";

    /// <summary>Размер чанка кадров при обработке длинных роликов (не жёсткий лимит всей длины).</summary>
    public int MaxVideoFrames { get; set; } = 300;

    /// <summary>Минимальный confidence OCR/детекции для принятия номера (0..1). На регистраторе типично 0.65–0.85.</summary>
    public double PlateMinConfidence { get; set; } = 0.70;

    public string NomeroffApiBaseUrl { get; set; } = "http://127.0.0.1:8000";

    public long MaxVideoUploadBytes { get; set; } = 1024L * 1024 * 1024;
}

public sealed class FolderWatchOptions
{
    public const string Section = "FolderWatch";

    public bool Enabled { get; set; }
    public string WatchFolder { get; set; } = @"D:\REG_VIDEO";
    public string AfterAction { get; set; } = "Move";
    public string MoveSubfolder { get; set; } = "Processed";
    public int IntervalSec { get; set; } = 2;
    public bool SaveToDb { get; set; } = true;
    public int DedupIntervalSec { get; set; } = 300;
    public bool SkipSaveWithoutGps { get; set; }
    public string DeviceName { get; set; } = "";
    public int PollSeconds { get; set; } = 5;
    public int StableSeconds { get; set; } = 2;

    /// <summary>Подпапка (или абсолютный путь) для disk-queue при недоступности OCR/IB.</summary>
    public string DiskQueueSubfolder { get; set; } = "_disk_queue";
}
