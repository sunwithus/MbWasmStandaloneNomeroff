using Nomeroff.Shared;
using Xunit;

namespace Nomeroff.Tests;

public class PlateTimeDedupTests
{
    [Fact]
    public void SamePlateWithinInterval_IsDuplicate()
    {
        var last = new Dictionary<string, double>(StringComparer.Ordinal);
        Assert.False(PlateTimeDedup.IsDuplicateAndTouch(last, "А123ВС77", 1.0, 10));
        Assert.True(PlateTimeDedup.IsDuplicateAndTouch(last, "А123ВС199", 8.0, 10));
    }

    [Fact]
    public void SamePlateAfterInterval_IsKept()
    {
        var last = new Dictionary<string, double>(StringComparer.Ordinal);
        Assert.False(PlateTimeDedup.IsDuplicateAndTouch(last, "А123ВС77", 1.0, 10));
        Assert.False(PlateTimeDedup.IsDuplicateAndTouch(last, "А123ВС77", 12.0, 10));
    }

    [Fact]
    public void SlidingWindow_KeepsSuppressingWhileSightingsAreDense()
    {
        var last = new Dictionary<string, double>(StringComparer.Ordinal);
        Assert.False(PlateTimeDedup.IsDuplicateAndTouch(last, "Х034ХА125", 0.0, 10));
        Assert.True(PlateTimeDedup.IsDuplicateAndTouch(last, "Х034ХА125", 8.0, 10));
        Assert.True(PlateTimeDedup.IsDuplicateAndTouch(last, "Х034ХА125", 16.0, 10));
    }

    [Fact]
    public void IntervalZero_NeverDedups()
    {
        var last = new Dictionary<string, double>(StringComparer.Ordinal);
        Assert.False(PlateTimeDedup.IsDuplicateAndTouch(last, "А123ВС77", 1.0, 0));
        Assert.False(PlateTimeDedup.IsDuplicateAndTouch(last, "А123ВС77", 1.5, 0));
    }

    [Fact]
    public void DifferentStems_AreIndependent()
    {
        var last = new Dictionary<string, double>(StringComparer.Ordinal);
        Assert.False(PlateTimeDedup.IsDuplicateAndTouch(last, "А123ВС77", 1.0, 10));
        Assert.False(PlateTimeDedup.IsDuplicateAndTouch(last, "В713ВВ125", 2.0, 10));
    }
}
