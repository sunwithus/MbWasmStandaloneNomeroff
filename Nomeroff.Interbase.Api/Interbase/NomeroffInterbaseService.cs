using InterBaseSql.Data.InterBaseClient;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Nomeroff.Interbase.Api.Interbase;

/// <summary>
/// Сервис для записи распознанных номеров в Interbase.
/// ID через GENERATOR; S_DATETIME из time_utc (fallback Now); пагинация FIRST/SKIP.
/// </summary>
public class NomeroffInterbaseService
{
    public const string DefaultGeneratorName = "GEN_SPR_SPEECH_INCKEY";

    private readonly string _connectionString;
    private readonly ILogger? _logger;
    private readonly string _generatorName;
    private static readonly HashSet<string> EnsuredGenerators = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object GeneratorLock = new();

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_connectionString);

    public NomeroffInterbaseService(string? connectionString, ILogger? logger = null, string? generatorName = null)
    {
        _connectionString = connectionString ?? "";
        _logger = logger;
        _generatorName = string.IsNullOrWhiteSpace(generatorName) ? DefaultGeneratorName : generatorName.Trim();
    }

    public NomeroffInterbaseService WithConnection(string connectionString) =>
        new NomeroffInterbaseService(connectionString, _logger, _generatorName);

    private static IBConnection GetConnection(string connectionString)
    {
        var conn = new IBConnection(connectionString);
        conn.Open();
        return conn;
    }

    private static void CloseConnection(IBConnection? conn)
    {
        if (conn != null && conn.State == ConnectionState.Open)
        {
            conn.Close();
            conn.Dispose();
        }
    }

    private static string ToWin1251(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        try
        {
            byte[] bytes = Encoding.GetEncoding(1251).GetBytes(input);
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
        catch
        {
            return new string(input.Where(c => c <= 127 || char.IsLetterOrDigit(c)).ToArray());
        }
    }

    /// <summary>Разобрать time_utc (ISO) → локальный DateTime для S_DATETIME; иначе DateTime.Now.</summary>
    public static DateTime ResolveDateTime(string? timeUtc)
    {
        if (string.IsNullOrWhiteSpace(timeUtc))
            return DateTime.Now;

        if (DateTimeOffset.TryParse(timeUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dto))
            return dto.ToLocalTime().DateTime;

        if (DateTime.TryParse(timeUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dt))
        {
            if (dt.Kind == DateTimeKind.Utc)
                return dt.ToLocalTime();
            if (dt.Kind == DateTimeKind.Unspecified)
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
            return dt;
        }

        return DateTime.Now;
    }

    private async Task EnsureGeneratorAsync(IBConnection conn, CancellationToken ct)
    {
        var cacheKey = _connectionString + "|" + _generatorName;
        lock (GeneratorLock)
        {
            if (EnsuredGenerators.Contains(cacheKey))
                return;
        }

        // Проверка/создание генератора без TRIM/@ (старый InterBase)
        var genNameUpper = _generatorName.ToUpperInvariant();
        if (!Regex.IsMatch(genNameUpper, @"^[A-Z][A-Z0-9_]*$"))
            throw new InvalidOperationException($"Некорректное имя GENERATOR: {_generatorName}");

        var exists = false;
        using (var listCmd = new IBCommand("SELECT RDB$GENERATOR_NAME FROM RDB$GENERATORS", conn))
        {
            await using var reader = await listCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0)?.Trim() ?? "";
                if (string.Equals(name, genNameUpper, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            try
            {
                using var create = new IBCommand($"CREATE GENERATOR {_generatorName}", conn);
                await create.ExecuteNonQueryAsync(ct);
                _logger?.LogInformation("Создан GENERATOR {Name}", _generatorName);
            }
            catch (Exception ex) when (ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase)
                                       || ex.Message.Contains("exist", StringComparison.OrdinalIgnoreCase)
                                       || ex.Message.Contains("уже", StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogDebug(ex, "GENERATOR {Name} уже есть", _generatorName);
            }
        }

        // Выровнять значение генератора не ниже MAX(S_INCKEY)
        long maxKey = 0;
        using (var maxCmd = new IBCommand("SELECT MAX(S_INCKEY) FROM SPR_SPEECH_TABLE", conn))
        {
            var r = await maxCmd.ExecuteScalarAsync(ct);
            if (r != null && r != DBNull.Value)
                maxKey = Convert.ToInt64(r);
        }

        long genVal = 0;
        using (var genCmd = new IBCommand($"SELECT GEN_ID({_generatorName}, 0) FROM RDB$DATABASE", conn))
        {
            var r = await genCmd.ExecuteScalarAsync(ct);
            if (r != null && r != DBNull.Value)
                genVal = Convert.ToInt64(r);
        }

        if (genVal < maxKey)
        {
            using var setCmd = new IBCommand($"SET GENERATOR {_generatorName} TO {maxKey}", conn);
            await setCmd.ExecuteNonQueryAsync(ct);
            _logger?.LogInformation("GENERATOR {Name} выровнен до {Max}", _generatorName, maxKey);
        }

        lock (GeneratorLock)
            EnsuredGenerators.Add(cacheKey);
    }

    private async Task<long> NextKeyAsync(IBConnection conn, IBTransaction transaction, CancellationToken ct)
    {
        using var cmd = new IBCommand($"SELECT GEN_ID({_generatorName}, 1) FROM RDB$DATABASE", conn, transaction);
        var r = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(r);
    }

    /// <summary>Сохранить запись о распознанном номере.</summary>
    public async Task<long> SaveRecordAsync(
        string deviceId,
        string notice,
        double? latitude,
        double? longitude,
        byte[]? screenshotBlob,
        CancellationToken ct = default,
        string? timeUtc = null)
    {
        _logger?.LogInformation(
            "SaveRecordAsync: deviceId={DeviceId}, notice={Notice}, hasCoords={HasCoords}, imageLen={ImageLen}, timeUtc={TimeUtc}",
            deviceId, notice, latitude.HasValue && longitude.HasValue, screenshotBlob?.Length ?? 0, timeUtc);
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _logger?.LogWarning("SaveRecordAsync: Interbase не настроен.");
            throw new InvalidOperationException("Interbase не настроен.");
        }

        IBConnection? conn = null;
        try
        {
            conn = GetConnection(_connectionString);
            await EnsureGeneratorAsync(conn, ct);

            long newKey;
            using (var transaction = conn.BeginTransaction())
            {
                newKey = await NextKeyAsync(conn, transaction, ct);
                _logger?.LogInformation("SaveRecordAsync: newKey={NewKey} (GENERATOR)", newKey);

                var when = ResolveDateTime(timeUtc);
                var deviceIdWin = ToWin1251(deviceId ?? "NOMEROFF");
                var noticeWin = ToWin1251(notice ?? "");

                var sql = @"
            INSERT INTO SPR_SPEECH_TABLE (
                S_INCKEY, S_TYPE, S_PRELOOKED, S_DATETIME, S_NOTICE, S_DEVICEID, 
                S_CALLTYPE, S_SELSTATUS
            ) VALUES (
                @S_INCKEY, @S_TYPE, @S_PRELOOKED, @S_DATETIME, @S_NOTICE, @S_DEVICEID, 
                @S_CALLTYPE, @S_SELSTATUS
            )";

                using (var command = new IBCommand(sql, conn, transaction))
                {
                    command.Parameters.Add("@S_INCKEY", IBDbType.BigInt).Value = newKey;
                    command.Parameters.AddWithValue("@S_TYPE", IBDbType.Integer).Value = 0;
                    command.Parameters.AddWithValue("@S_PRELOOKED", IBDbType.Integer).Value = 0;
                    command.Parameters.AddWithValue("@S_DEVICEID", IBDbType.VarChar).Value = deviceIdWin;
                    command.Parameters.AddWithValue("@S_DATETIME", IBDbType.TimeStamp).Value = when;
                    command.Parameters.AddWithValue("@S_NOTICE", IBDbType.VarChar).Value = noticeWin;
                    command.Parameters.AddWithValue("@S_CALLTYPE", IBDbType.Integer).Value = 2;
                    command.Parameters.AddWithValue("@S_SELSTATUS", IBDbType.SmallInt).Value = 0;

                    await command.ExecuteNonQueryAsync(ct);
                }

                _logger?.LogInformation("SaveRecordAsync: SPR_SPEECH_TABLE OK, S_DATETIME={When}", when);

                if (latitude.HasValue && longitude.HasValue)
                {
                    var geoSql = @"
                    INSERT INTO SPR_SP_GEO_TABLE (S_INCKEY, S_ORDER, S_LATITUDE, S_LONGITUDE)
                    VALUES (@Key, @Order, @Lat, @Lon)";
                    using (var cmd = new IBCommand(geoSql, conn, transaction))
                    {
                        cmd.Parameters.Add("@Key", IBDbType.BigInt).Value = newKey;
                        cmd.Parameters.AddWithValue("@Order", IBDbType.Integer).Value = 0;
                        cmd.Parameters.AddWithValue("@Lat", IBDbType.Double).Value = latitude.Value;
                        cmd.Parameters.AddWithValue("@Lon", IBDbType.Double).Value = longitude.Value;
                        await cmd.ExecuteNonQueryAsync(ct);
                    }
                    _logger?.LogInformation("SaveRecordAsync: SPR_SP_GEO_TABLE OK");
                }

                if (screenshotBlob != null && screenshotBlob.Length > 0)
                {
                    _logger?.LogInformation("SaveRecordAsync: inserting F_IMAGE, size={Size}", screenshotBlob.Length);
                    var fotoSql = @"
                    INSERT INTO SPR_SP_FOTO_TABLE (S_INCKEY, F_IMAGE)
                    VALUES (@Key, @Image)";
                    using (var cmd = new IBCommand(fotoSql, conn, transaction))
                    {
                        cmd.Parameters.Add("@Key", IBDbType.BigInt).Value = newKey;
                        cmd.Parameters.Add("@Image", IBDbType.Binary).Value = screenshotBlob;
                        await cmd.ExecuteNonQueryAsync(ct);
                    }
                    _logger?.LogInformation("SaveRecordAsync: SPR_SP_FOTO_TABLE OK");
                }

                transaction.Commit();
                _logger?.LogInformation("SaveRecordAsync: success, id={Id}", newKey);
                return newKey;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SaveRecordAsync: ошибка");
            throw;
        }
        finally
        {
            CloseConnection(conn);
        }
    }

    /// <summary>Получить записи с пагинацией (совместимо со старым InterBase без FIRST/SKIP).</summary>
    public async Task<(IReadOnlyList<DbRecordDto> Records, int Total)> GetRecordsPageAsync(
        int limit = 100,
        int offset = 0,
        CancellationToken ct = default)
    {
        _logger?.LogInformation("GetRecordsPageAsync: limit={Limit}, offset={Offset}", limit, offset);
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _logger?.LogWarning("GetRecordsPageAsync: Interbase не настроен.");
            return (Array.Empty<DbRecordDto>(), 0);
        }

        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);

        IBConnection? conn = null;
        try
        {
            conn = GetConnection(_connectionString);

            int total;
            using (var countCmd = new IBCommand("SELECT COUNT(*) FROM SPR_SPEECH_TABLE", conn))
            {
                total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct) ?? 0);
            }

            // Старый InterBase: без FIRST/SKIP — берём только ID, режем страницу в C#, детали — по IN.
            var allIds = new List<long>(Math.Min(total, 50_000));
            using (var idCmd = new IBCommand("SELECT S_INCKEY FROM SPR_SPEECH_TABLE ORDER BY S_INCKEY DESC", conn))
            {
                await using var reader = await idCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    allIds.Add(Convert.ToInt64(reader["S_INCKEY"]));
            }

            var pageIds = allIds.Skip(offset).Take(limit).ToList();
            if (pageIds.Count == 0)
            {
                _logger?.LogInformation("GetRecordsPageAsync: пустая страница, total={Total}", total);
                return (Array.Empty<DbRecordDto>(), total);
            }

            const int inChunkSize = 100;
            var result = new List<DbRecordDto>();
            for (var i = 0; i < pageIds.Count; i += inChunkSize)
            {
                var chunk = pageIds.Skip(i).Take(inChunkSize).ToList();
                var inClause = string.Join(", ", chunk);
                var sqlData = $@"
SELECT s.S_INCKEY, s.S_DEVICEID, s.S_DATETIME, s.S_NOTICE, g.S_LATITUDE, g.S_LONGITUDE
FROM SPR_SPEECH_TABLE s
LEFT JOIN SPR_SP_GEO_TABLE g ON g.S_INCKEY = s.S_INCKEY
WHERE s.S_INCKEY IN ({inClause})
ORDER BY s.S_INCKEY DESC";

                using var cmd = new IBCommand(sqlData, conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    result.Add(new DbRecordDto
                    {
                        S_INCKEY = Convert.ToInt64(reader["S_INCKEY"]),
                        S_DEVICEID = reader["S_DEVICEID"]?.ToString() ?? "",
                        S_DATETIME = reader["S_DATETIME"] is DBNull ? null : Convert.ToDateTime(reader["S_DATETIME"]),
                        S_NOTICE = reader["S_NOTICE"]?.ToString() ?? "",
                        S_LATITUDE = reader["S_LATITUDE"] is DBNull ? null : Convert.ToDouble(reader["S_LATITUDE"]),
                        S_LONGITUDE = reader["S_LONGITUDE"] is DBNull ? null : Convert.ToDouble(reader["S_LONGITUDE"]),
                        HasFoto = false
                    });
                }
            }

            var ordered = result.OrderByDescending(r => r.S_INCKEY).ToList();
            _logger?.LogInformation("GetRecordsPageAsync: page={Count}, total={Total}", ordered.Count, total);
            return (ordered, total);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "GetRecordsPageAsync: ошибка");
            throw;
        }
        finally
        {
            CloseConnection(conn);
        }
    }

    /// <summary>Совместимость: только страница записей.</summary>
    public async Task<IReadOnlyList<DbRecordDto>> GetRecordsAsync(int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        var (records, _) = await GetRecordsPageAsync(limit, offset, ct);
        return records;
    }

    public async Task<byte[]?> GetImageAsync(long sInckey, CancellationToken ct = default)
    {
        _logger?.LogInformation("GetImageAsync: sInckey={Key}", sInckey);
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _logger?.LogWarning("GetImageAsync: Interbase не настроен.");
            return null;
        }

        IBConnection? conn = null;
        try
        {
            conn = GetConnection(_connectionString);
            var sql = "SELECT F_IMAGE FROM SPR_SP_FOTO_TABLE WHERE S_INCKEY = @Key";
            using (var cmd = new IBCommand(sql, conn))
            {
                cmd.Parameters.Add("@Key", IBDbType.BigInt).Value = sInckey;
                var r = await cmd.ExecuteScalarAsync(ct);
                if (r is byte[] bytes && bytes.Length > 0)
                {
                    _logger?.LogInformation("GetImageAsync: получено {Len} байт", bytes.Length);
                    return bytes;
                }
            }
            _logger?.LogInformation("GetImageAsync: изображение не найдено");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "GetImageAsync: ошибка");
            return null;
        }
        finally
        {
            CloseConnection(conn);
        }
    }

    public async Task<bool> DeleteRecordAsync(long sInckey, CancellationToken ct = default)
    {
        _logger?.LogInformation("DeleteRecordAsync: sInckey={Key}", sInckey);
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _logger?.LogWarning("DeleteRecordAsync: Interbase не настроен.");
            return false;
        }

        IBConnection? conn = null;
        try
        {
            conn = GetConnection(_connectionString);
            foreach (var table in new[] { "SPR_SP_FOTO_TABLE", "SPR_SP_GEO_TABLE", "SPR_SPEECH_TABLE" })
            {
                using (var cmd = new IBCommand($"DELETE FROM {table} WHERE S_INCKEY = @Key", conn))
                {
                    cmd.Parameters.Add("@Key", IBDbType.BigInt).Value = sInckey;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }
            _logger?.LogInformation("DeleteRecordAsync: success");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "DeleteRecordAsync: ошибка");
            return false;
        }
        finally
        {
            CloseConnection(conn);
        }
    }

    public async Task<(bool success, string message)> TestConnectionAsync(CancellationToken ct = default)
    {
        _logger?.LogInformation("TestConnectionAsync");
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _logger?.LogWarning("TestConnectionAsync: Interbase не настроен.");
            return (false, "Interbase не настроен.");
        }

        IBConnection? conn = null;
        try
        {
            conn = GetConnection(_connectionString);
            await EnsureGeneratorAsync(conn, ct);
            using (var cmd = new IBCommand($"SELECT GEN_ID({_generatorName}, 0) FROM RDB$DATABASE", conn))
            {
                var r = await cmd.ExecuteScalarAsync(ct);
                var gen = r != null && r != DBNull.Value ? Convert.ToInt64(r) : 0;
                _logger?.LogInformation("TestConnectionAsync: OK, GEN={Gen}", gen);
                return (true, $"Подключение OK. GENERATOR {_generatorName} = {gen}");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "TestConnectionAsync: ошибка");
            return (false, $"Ошибка: {ex.Message}");
        }
        finally
        {
            CloseConnection(conn);
        }
    }
}

public class DbRecordDto
{
    public long S_INCKEY { get; set; }
    public string S_DEVICEID { get; set; } = "";
    public DateTime? S_DATETIME { get; set; }
    public string S_NOTICE { get; set; } = "";
    public double? S_LATITUDE { get; set; }
    public double? S_LONGITUDE { get; set; }
    public bool HasFoto { get; set; }
}
