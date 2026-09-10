using Nomeroff.Interbase.Api.Interbase;
using Xunit;

namespace Nomeroff.Tests;

public class DailyDbTests
{
    [Fact]
    public void DailyFileName_UsesLocalCalendarDate()
        => Assert.Equal("2026-09-10.IBS", DbManager.DailyFileName(new DateTime(2026, 9, 10, 23, 59, 0)));

    [Fact]
    public async Task EnsureDatabase_CreatesFromEmptyArchiveWhenMissing()
    {
        var zip = Path.Combine(AppContext.BaseDirectory, "empty38.zip");
        Assert.True(File.Exists(zip), "empty38.zip должен копироваться в выход тестов");

        var dir = Path.Combine(Path.GetTempPath(), "ib_daily_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var mgr = new DbManager(dir, zip);
            var name = DbManager.DailyFileName(new DateTime(2026, 9, 10));

            var first = await mgr.EnsureDatabaseAsync(name);
            Assert.True(first.exists, first.message);
            Assert.True(first.created);
            Assert.Equal("2026-09-10.IBS", first.fileName);
            Assert.True(mgr.DatabaseExists(name));
            Assert.True(new FileInfo(Path.Combine(dir, name)).Length > 1000);

            var again = await mgr.EnsureDatabaseAsync(name);
            Assert.True(again.exists);
            Assert.False(again.created);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
