using Nomeroff.Shared;
using Xunit;

namespace Nomeroff.Tests;

public class PlateVoteTests
{
    private static PlateReading Read(string plate, double timeSec, double prob = 0.9) => new()
    {
        Plate = plate,
        CharProbs = Enumerable.Repeat(prob, plate.Length).ToArray(),
        OcrConfidence = prob,
        TimeSec = timeSec
    };

    [Fact]
    public void Vote_PicksMajorityGlyphPerPosition()
    {
        var result = PlateVote.Vote(new[]
        {
            Read("В713ВВ125", 1.0),
            Read("В713ВВ125", 1.4),
            Read("В713НВ125", 1.8)
        });

        Assert.NotNull(result);
        Assert.Equal("В713ВВ125", result!.Plate);
        Assert.Equal(3, result.FrameHits);
    }

    /// <summary>
    /// Одно уверенное чтение перевешивает одно неуверенное: именно за это
    /// в конвейер тянутся char_probs из CTC-головы.
    /// </summary>
    [Fact]
    public void Vote_WeighsByCharProbability()
    {
        var result = PlateVote.Vote(new[]
        {
            Read("Х034ХА125", 1.0, prob: 0.98),
            Read("Х084ХА125", 1.4, prob: 0.30)
        });

        Assert.Equal("Х034ХА125", result!.Plate);
    }

    /// <summary>Смешивать разные длины по позициям нельзя — «уедет» регион.</summary>
    [Fact]
    public void Vote_DoesNotMixDifferentLengths()
    {
        var result = PlateVote.Vote(new[]
        {
            Read("А123ВС125", 1.0),
            Read("А123ВС125", 1.4),
            Read("А123ВС12", 1.8)
        });

        Assert.Equal("А123ВС125", result!.Plate);
        Assert.Equal(2, result.FrameHits);
    }

    [Fact]
    public void Vote_ReturnsNullWithoutUsableReadings()
    {
        Assert.Null(PlateVote.Vote(Array.Empty<PlateReading>()));
        Assert.Null(PlateVote.Vote(new[] { Read("", 1.0) }));
    }

    [Fact]
    public void Vote_CountsRepeatedTimestampAsOneFrame()
    {
        var result = PlateVote.Vote(new[]
        {
            Read("В713ВВ125", 1.0),
            Read("В713ВВ125", 1.0)
        });

        Assert.Equal(1, result!.FrameHits);
    }

    /// <summary>
    /// Настоящий номер повторяется дословно, поэтому AgreeingFrameHits близок
    /// к FrameHits.
    /// </summary>
    [Fact]
    public void Vote_CountsFramesThatAgreeWithVotedText()
    {
        var result = PlateVote.Vote(new[]
        {
            Read("Х034ХА125", 1.0),
            Read("Х034ХА125", 1.2),
            Read("Х084ХА125", 1.4, prob: 0.3)
        });

        Assert.Equal("Х034ХА125", result!.Plate);
        Assert.Equal(3, result.FrameHits);
        Assert.Equal(2, result.AgreeingFrameHits);
    }

    /// <summary>
    /// Вывеска «АВАРИЙНАЯ» на борту: детекция в каждом кадре, но текст каждый
    /// раз другой. Число кадров трека тут ни о чём не говорит — важно, что ни
    /// одно чтение не повторилось.
    /// </summary>
    [Fact]
    public void Vote_ReportsSingleAgreeingFrameForJunkReadings()
    {
        var result = PlateVote.Vote(new[]
        {
            Read("А835НМ69", 38.4, prob: 0.62),
            Read("А839НМ69", 38.4, prob: 0.60),
            Read("А840НМ69", 38.6, prob: 0.64),
            Read("А849НМ69", 38.6, prob: 0.63)
        });

        Assert.Equal(2, result!.FrameHits);
        Assert.Equal(1, result.AgreeingFrameHits);
    }
}

public class PlateTrackerTests
{
    private static TrackDetection Det(string plate, double timeSec, int[]? bbox, int area = 0) => new()
    {
        Plate = plate,
        TimeSec = timeSec,
        Bbox = bbox,
        BboxArea = area != 0 ? area : Area(bbox)
    };

    private static int Area(int[]? b) =>
        b == null ? 0 : Math.Abs(b[2] - b[0]) * Math.Abs(b[3] - b[1]);

    /// <summary>
    /// Ошибка OCR на одном кадре не должна порождать «вторую машину»: раньше
    /// группировка шла по тексту, и такая опечатка давала лишнюю запись в БД.
    /// </summary>
    [Fact]
    public void Add_KeepsOneTrackWhenOcrMisreadsSingleFrame()
    {
        var tracker = new PlateTracker();
        tracker.Add(Det("В713ВВ125", 1.0, new[] { 900, 600, 1000, 630 }));
        tracker.Add(Det("В713НВ129", 1.33, new[] { 895, 605, 1000, 638 }));
        tracker.Add(Det("В713ВВ125", 1.66, new[] { 890, 610, 1005, 645 }));

        Assert.Single(tracker.Tracks);
        Assert.Equal(3, tracker.Tracks[0].FrameHits);
    }

    /// <summary>
    /// Приближающаяся машина: бокс растёт и уходит вбок, пересечения между
    /// кадрами уже нет. Раньше такой проезд рассыпался на одиночные треки, и
    /// порог MinFrameHits выбрасывал настоящие номера как фантомы.
    /// </summary>
    [Fact]
    public void Add_KeepsOneTrackWhenPlateMovesWithoutOverlap()
    {
        var tracker = new PlateTracker();
        tracker.Add(Det("Р789ОН125", 1.0, new[] { 960, 560, 1010, 575 }));
        tracker.Add(Det("Р789ОН125", 1.2, new[] { 1000, 600, 1070, 622 }));
        tracker.Add(Det("Р789ОН125", 1.4, new[] { 1060, 650, 1155, 680 }));

        Assert.Single(tracker.Tracks);
        Assert.Equal(3, tracker.Tracks[0].FrameHits);
    }

    /// <summary>
    /// Тот же проезд, но OCR каждый раз читает по-разному: склеить всё равно
    /// надо — какой номер у трека, решает голосование, а не совпадение текста.
    /// </summary>
    [Fact]
    public void Add_MergesMovingPlateEvenWhenTextDisagrees()
    {
        var tracker = new PlateTracker();
        tracker.Add(Det("Р789ОН125", 1.0, new[] { 960, 560, 1010, 575 }));
        tracker.Add(Det("Р789ОН126", 1.2, new[] { 1000, 600, 1070, 622 }));

        Assert.Single(tracker.Tracks);
    }

    [Fact]
    public void Add_SplitsDistinctCarsInSameFrame()
    {
        var tracker = new PlateTracker();
        tracker.Add(Det("В713ВВ125", 1.0, new[] { 200, 600, 300, 630 }));
        tracker.Add(Det("Х034ХА125", 1.0, new[] { 1400, 610, 1520, 645 }));

        Assert.Equal(2, tracker.Tracks.Count);
    }

    [Fact]
    public void Add_StartsNewTrackAfterTimeGap()
    {
        var tracker = new PlateTracker(maxGapSec: 3.0);
        tracker.Add(Det("В713ВВ125", 1.0, new[] { 900, 600, 1000, 630 }));
        tracker.Add(Det("В713ВВ125", 40.0, new[] { 900, 600, 1000, 630 }));

        Assert.Equal(2, tracker.Tracks.Count);
    }

    /// <summary>Кадр для БД берётся там, где номер крупнее — машина ближе к камере.</summary>
    [Fact]
    public void Best_PicksLargestBbox()
    {
        var tracker = new PlateTracker();
        tracker.Add(Det("В713ВВ125", 1.0, new[] { 940, 600, 1000, 620 }));
        tracker.Add(Det("В713ВВ125", 1.33, new[] { 900, 590, 1020, 650 }));
        tracker.Add(Det("В713ВВ125", 1.66, new[] { 930, 600, 1000, 625 }));

        Assert.Equal(1.33, tracker.Tracks[0].Best.TimeSec);
    }

    /// <summary>
    /// У края кадра встречная машина за кадр проходит три своих ширины.
    /// Раньше трек здесь рвался, и один проезд давал две записи в БД.
    /// </summary>
    [Fact]
    public void Add_KeepsOneTrackWhenSameTextJumpsNearFrameEdge()
    {
        var tracker = new PlateTracker();
        tracker.Add(Det("Х034ХА125", 20.8, new[] { 1515, 501, 1584, 519 }));
        tracker.Add(Det("Х034ХА125", 21.0, new[] { 1662, 527, 1728, 551 }));
        tracker.Add(Det("Х034ХА125", 21.2, new[] { 1866, 567, 1920, 603 }));

        Assert.Single(tracker.Tracks);
        Assert.Equal(3, tracker.Tracks[0].FrameHits);
    }

    /// <summary>Без bbox (живая камера) остаётся текстовая привязка в окне времени.</summary>
    [Fact]
    public void Add_FallsBackToTextWhenBboxMissing()
    {
        var tracker = new PlateTracker();
        tracker.Add(Det("В713ВВ125", 1.0, null));
        tracker.Add(Det("В713ВВ125", 1.5, null));

        Assert.Single(tracker.Tracks);
    }
}
