using System.IO.Compression;
using InterBaseSql.Data.InterBaseClient;

namespace Nomeroff.Interbase.Api.Interbase;

/// <summary>Управление БД: список, создание из архива empty38.zip.</summary>
public class DbManager
{
    private readonly string _dbFolder;
    private readonly string _archivePath;
    private const string DefaultConnectionTemplate = "User=SYSDBA;Password=masterkey;Database={0};DataSource=localhost;Port=3050;Dialect=3;Charset=NONE;Role=;Connection lifetime=15;Pooling=true;MinPoolSize=0;MaxPoolSize=50;Packet Size=8192;ServerType=0";

    public DbManager(string? dbFolder, string? archivePath)
    {
        _dbFolder = Path.GetFullPath(dbFolder ?? Path.Combine(AppContext.BaseDirectory, "Examples"));
        _archivePath = Path.GetFullPath(archivePath ?? Path.Combine(AppContext.BaseDirectory, "empty38.zip"));
    }

    /// <summary>Получить список .IBS файлов в папке БД.</summary>
    public IReadOnlyList<string> ListDatabases()
    {
        if (!Directory.Exists(_dbFolder))
            return Array.Empty<string>();
        var files = Directory.GetFiles(_dbFolder, "*.IBS")
            .Select(Path.GetFileName)
            .Where(f => f != null)
            .Cast<string>()
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return files;
    }

    /// <summary>Построить connection string по имени файла БД.</summary>
    public string GetConnectionString(string dbFileName)
    {
        var fullPath = Path.Combine(_dbFolder, dbFileName);
        return string.Format(DefaultConnectionTemplate, fullPath);
    }

    /// <summary>Создать новую БД копированием из архива empty38.zip (извлекается EMPTY38.IBS).</summary>
    /// <param name="newFileName">Имя нового файла, например MyDb.IBS</param>
    public async Task<(bool success, string message)> CreateFromArchiveAsync(string newFileName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newFileName))
            return (false, "Укажите имя файла.");
        if (!newFileName.EndsWith(".IBS", StringComparison.OrdinalIgnoreCase))
            newFileName += ".IBS";

        var destPath = Path.Combine(_dbFolder, newFileName);
        if (File.Exists(destPath))
            return (false, $"Файл {newFileName} уже существует.");

        if (!File.Exists(_archivePath))
            return (false, $"Архив не найден: {Path.GetFileName(_archivePath)}");

        try
        {
            Directory.CreateDirectory(_dbFolder);
            await Task.Run(() =>
            {
                using var zip = ZipFile.OpenRead(_archivePath);
                var entry = zip.GetEntry("EMPTY38.IBS") ?? zip.Entries.FirstOrDefault(e =>
                    e.Name.EndsWith(".IBS", StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                    throw new InvalidOperationException("В архиве не найден файл .IBS");
                var extractPath = Path.Combine(_dbFolder, entry.Name);
                entry.ExtractToFile(extractPath, overwrite: false);
                if (!Path.GetFullPath(extractPath).Equals(Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(extractPath, destPath);
                }
            }, ct);
            return (true, $"БД создана: {newFileName}");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
            return (false, ex.Message);
        }
    }

    /// <summary>Закрыть все соединения с указанной БД (очистить пул) перед удалением.</summary>
    private void CloseConnectionsTo(string dbFileName)
    {
        try
        {
            var connStr = GetConnectionString(dbFileName);
            using var conn = new IBConnection(connStr);
            conn.Open();
            conn.Close();
            IBConnection.ClearPool(conn);
        }
        catch { /* игнорируем — удаление всё равно попробуем */ }
    }

    /// <summary>Удалить файл БД. Перед удалением закрываются соединения с этой БД.</summary>
    public (bool success, string message) DeleteDatabase(string dbFileName)
    {
        if (string.IsNullOrWhiteSpace(dbFileName))
            return (false, "Укажите имя файла.");
        if (!dbFileName.EndsWith(".IBS", StringComparison.OrdinalIgnoreCase))
            return (false, "Только файлы .IBS");
        var path = Path.Combine(_dbFolder, Path.GetFileName(dbFileName));
        if (!File.Exists(path))
            return (false, $"Файл не найден: {dbFileName}");
        try
        {
            CloseConnectionsTo(dbFileName);
            File.Delete(path);
            return (true, $"БД удалена: {dbFileName}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public string DbFolder => _dbFolder;
}
