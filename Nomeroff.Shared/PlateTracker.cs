namespace Nomeroff.Shared;

/// <summary>Детекция номера на одном кадре — вход трекера.</summary>
public sealed class TrackDetection
{
    public required string Plate { get; init; }
    public IReadOnlyList<double> CharProbs { get; init; } = Array.Empty<double>();
    public double OcrConfidence { get; init; }
    public double DetConfidence { get; init; }
    public double TimeSec { get; init; }
    /// <summary>[x1,y1,x2,y2] в координатах исходного кадра; null — трекинг только по тексту.</summary>
    public int[]? Bbox { get; init; }
    public int BboxArea { get; init; }
    public string? FrameImageBase64 { get; init; }
    public string? PlateImageBase64 { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? TimeUtc { get; init; }
}

/// <summary>Одна машина: все чтения её номера за проезд.</summary>
public sealed class PlateTrack
{
    public int Id { get; init; }
    public List<TrackDetection> Detections { get; } = new();

    public double FirstTimeSec => Detections.Count > 0 ? Detections[0].TimeSec : 0;
    public double LastTimeSec => Detections.Count > 0 ? Detections[^1].TimeSec : 0;
    public int FrameHits => Detections.Select(d => Math.Round(d.TimeSec, 2)).Distinct().Count();

    /// <summary>Кадр, где номер крупнее всего: машина ближе к камере, а не уже уезжает.</summary>
    public TrackDetection Best => Detections.Count == 0
        ? throw new InvalidOperationException("Трек пуст")
        : Detections.Aggregate((a, b) => b.BboxArea > a.BboxArea ? b : a);

    /// <summary>
    /// Фото/кроп только среди чтений этого номера (или того же ствола).
    /// Иначе в плотном потоке в БД уезжает кадр соседней машины с более крупным bbox.
    /// </summary>
    public TrackDetection? BestForPlate(string plate)
    {
        if (Detections.Count == 0)
            return null;
        var exact = Detections
            .Where(d => string.Equals(d.Plate, plate, StringComparison.Ordinal))
            .ToList();
        if (exact.Count > 0)
            return exact.Aggregate((a, b) => b.BboxArea > a.BboxArea ? b : a);

        var stem = PlateAlphabet.DedupStem(plate);
        if (string.IsNullOrEmpty(stem))
            return null;
        var sameStem = Detections
            .Where(d => PlateAlphabet.DedupStem(d.Plate) == stem)
            .ToList();
        return sameStem.Count == 0
            ? null
            : sameStem.Aggregate((a, b) => b.BboxArea > a.BboxArea ? b : a);
    }

    internal int[]? LastBbox;
    internal double LastSeenSec;
}

/// <summary>
/// Трекинг машин по пересечению bbox между кадрами.
///
/// Раньше чтения группировались по «стволу» текста, поэтому одна ошибка OCR
/// создавала «новую машину» и лишнюю запись в БД. Здесь чтения привязываются
/// к геометрии: один проезд — один трек — одна запись, а какой именно номер
/// у этого трека, решает голосование.
///
/// Порог IoU низкий намеренно: между кадрами при 3 fps номер успевает
/// сместиться на свой размер, поэтому дополнительно допускается близость
/// центров относительно размера бокса.
/// </summary>
public sealed class PlateTracker
{
    private readonly double _iouThreshold;
    private readonly double _maxGapSec;
    private readonly double _centerDistanceFactor;
    private readonly double _sameTextDistanceFactor;
    private readonly List<PlateTrack> _tracks = new();
    private int _nextId = 1;

    /// <summary>
    /// Порог IoU низкий: на 3-5 fps номер успевает сместиться на свой размер.
    ///
    /// sameTextDistanceFactor заметно больше: у края кадра встречная машина за
    /// кадр проходит три своих ширины, и трек рвался на два — одна машина
    /// давала две записи в БД. Совпадение текста двух разных машин исключено,
    /// поэтому такому склеиванию можно позволить больший радиус.
    /// </summary>
    public PlateTracker(
        double iouThreshold = 0.08,
        double maxGapSec = 3.0,
        double centerDistanceFactor = 3.0,
        double sameTextDistanceFactor = 6.0)
    {
        _iouThreshold = iouThreshold;
        _maxGapSec = maxGapSec;
        _centerDistanceFactor = centerDistanceFactor;
        _sameTextDistanceFactor = Math.Max(centerDistanceFactor, sameTextDistanceFactor);
    }

    public IReadOnlyList<PlateTrack> Tracks => _tracks;

    public void Add(TrackDetection detection)
    {
        var track = FindTrack(detection);
        if (track == null)
        {
            track = new PlateTrack { Id = _nextId++ };
            _tracks.Add(track);
        }
        track.Detections.Add(detection);
        track.LastBbox = detection.Bbox ?? track.LastBbox;
        track.LastSeenSec = detection.TimeSec;
    }

    private PlateTrack? FindTrack(TrackDetection detection)
    {
        PlateTrack? best = null;
        var bestScore = 0.0;
        PlateTrack? nearby = null;
        var nearbyDistance = double.MaxValue;

        foreach (var track in _tracks)
        {
            var gap = detection.TimeSec - track.LastSeenSec;
            if (gap > _maxGapSec)
                continue;

            // Без геометрии остаётся только текст: тот же ствол номера в окне времени.
            if (detection.Bbox == null || track.LastBbox == null)
            {
                if (SameStem(track, detection.Plate) && bestScore <= 0)
                    best = track;
                continue;
            }

            var iou = Iou(track.LastBbox, detection.Bbox);
            if (iou > bestScore && iou >= _iouThreshold)
            {
                bestScore = iou;
                best = track;
                continue;
            }

            // Пересечения уже нет: машина приближается, бокс растёт и уходит вбок.
            // Тот же ствол номера — сильный признак, и его хватает на весь maxGap.
            // Без совпадения текста склеиваем только соседние кадры и только
            // если бокс похож по размеру — иначе слипнутся две разные машины.
            var sameStem = SameStem(track, detection.Plate);
            var distance = CenterDistance(track.LastBbox, detection.Bbox);
            var reach = Math.Max(Width(track.LastBbox), Width(detection.Bbox))
                        * (sameStem ? _sameTextDistanceFactor : _centerDistanceFactor);
            if (distance > reach)
                continue;

            // Соседство без IoU и без того же ствола: в одном кадре две машины
            // в потоке (номер через 2–3 своих ширины) слипались в один трек —
            // в БД уезжал номер одной, фото более крупной соседней.
            if (!sameStem)
                continue;

            // текстовое совпадение важнее близости: ставим его вперёд по приоритету
            var rank = sameStem ? distance : distance + reach;
            if (rank < nearbyDistance)
            {
                nearbyDistance = rank;
                nearby = track;
            }
        }
        return best ?? nearby;
    }

    private static bool SameStem(PlateTrack track, string plate)
    {
        var stem = PlateAlphabet.DedupStem(plate);
        return track.Detections.Any(d => PlateAlphabet.DedupStem(d.Plate) == stem);
    }

    private static double CenterDistance(int[] a, int[] b)
    {
        var (ax, ay) = Center(a);
        var (bx, by) = Center(b);
        var dx = ax - bx;
        var dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static (double X, double Y) Center(int[] box) =>
        ((box[0] + box[2]) / 2.0, (box[1] + box[3]) / 2.0);

    private static double Width(int[] box) => Math.Abs(box[2] - box[0]);

    private static double Iou(int[] a, int[] b)
    {
        double ax1 = Math.Min(a[0], a[2]), ax2 = Math.Max(a[0], a[2]);
        double ay1 = Math.Min(a[1], a[3]), ay2 = Math.Max(a[1], a[3]);
        double bx1 = Math.Min(b[0], b[2]), bx2 = Math.Max(b[0], b[2]);
        double by1 = Math.Min(b[1], b[3]), by2 = Math.Max(b[1], b[3]);

        var ix = Math.Max(0, Math.Min(ax2, bx2) - Math.Max(ax1, bx1));
        var iy = Math.Max(0, Math.Min(ay2, by2) - Math.Max(ay1, by1));
        var inter = ix * iy;
        if (inter <= 0)
            return 0;

        var union = (ax2 - ax1) * (ay2 - ay1) + (bx2 - bx1) * (by2 - by1) - inter;
        return union > 0 ? inter / union : 0;
    }
}
