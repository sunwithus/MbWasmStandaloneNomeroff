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
    private static readonly HashSet<string> EnsuredFPlate = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object GeneratorLock = new();
    private static readonly object SchemaLock = new();

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
        var genNameUpper = _generatorName.ToUpperInvariant();
        if (!Regex.IsMatch(genNameUpper, @"^[A-Z][A-Z0-9_]*$"))
            throw new InvalidOperationException($"Некорректное имя GENERATOR: {_generatorName}");

        // Быстрый путь: генератор реально отвечает GEN_ID (кэш мог устареть после
        // пересоздания .IBS из empty38.zip с тем же путём).
        var cached = false;
        lock (GeneratorLock)
            cached = EnsuredGenerators.Contains(cacheKey);

        if (cached)
        {
            try
            {
                using var probe = new IBCommand($"SELECT GEN_ID({_generatorName}, 0) FROM RDB$DATABASE", conn);
                await probe.ExecuteScalarAsync(ct);
                return;
            }
            catch
            {
                lock (GeneratorLock)
                    EnsuredGenerators.Remove(cacheKey);
                _logger?.LogWarning(
                    "GENERATOR {Name} пропал (БД пересоздана?) — создаём заново",
                    _generatorName);
            }
        }

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
                                       || ex.Message.Contains("уже", StringComparison.OrdinalIgnoreCase)
                                       || ex.Message.Contains("defined", StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogDebug(ex, "GENERATOR {Name} уже есть", _generatorName);
            }
        }

        // Проверка, что GEN_ID реально работает (иначе кэш не ставим)
        try
        {
            using var genCmd = new IBCommand($"SELECT GEN_ID({_generatorName}, 0) FROM RDB$DATABASE", conn);
            await genCmd.ExecuteScalarAsync(ct);
        }
        catch (Exception ex)
        {
            // Повторная попытка CREATE (гонка / DDL не применился)
            _logger?.LogWarning(ex, "GENERATOR {Name} не отвечает после create — повтор", _generatorName);
            try
            {
                using var create2 = new IBCommand($"CREATE GENERATOR {_generatorName}", conn);
                await create2.ExecuteNonQueryAsync(ct);
            }
            catch { /* already */ }

            using var genCmd2 = new IBCommand($"SELECT GEN_ID({_generatorName}, 0) FROM RDB$DATABASE", conn);
            await genCmd2.ExecuteScalarAsync(ct);
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

    /// <summary>Сбросить кэш GENERATOR (после удаления/пересоздания .IBS).</summary>
    public static void InvalidateGeneratorCache(string? connectionString = null, string? generatorName = null)
    {
        lock (GeneratorLock)
        {
            if (string.IsNullOrEmpty(connectionString))
            {
                EnsuredGenerators.Clear();
                EnsuredFPlate.Clear();
                return;
            }

            var gen = string.IsNullOrWhiteSpace(generatorName) ? DefaultGeneratorName : generatorName.Trim();
            EnsuredGenerators.Remove(connectionString + "|" + gen);
            EnsuredFPlate.Remove(connectionString);
        }
    }

    private async Task<long> NextKeyAsync(IBConnection conn, IBTransaction transaction, CancellationToken ct)
    {
        using var cmd = new IBCommand($"SELECT GEN_ID({_generatorName}, 1) FROM RDB$DATABASE", conn, transaction);
        var r = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(r);
    }

    /// <summary>Формат для S_BELONG: «87%» (VARCHAR 30).</summary>
    public static string FormatBelongPercent(double? confidence01)
    {
        if (!confidence01.HasValue)
            return "";
        var pct = (int)Math.Round(Math.Clamp(confidence01.Value, 0.0, 1.0) * 100.0);
        return pct + "%";
    }

    private async Task EnsureFPlateColumnAsync(IBConnection conn, CancellationToken ct)
    {
        var cacheKey = _connectionString;
        lock (SchemaLock)
        {
            if (EnsuredFPlate.Contains(cacheKey))
                return;
        }

        var exists = false;
        using (var cmd = new IBCommand(@"
SELECT COUNT(*) FROM RDB$RELATION_FIELDS
WHERE RDB$RELATION_NAME = 'SPR_SP_FOTO_TABLE' AND RDB$FIELD_NAME = 'F_PLATE'", conn))
        {
            var r = await cmd.ExecuteScalarAsync(ct);
            exists = Convert.ToInt32(r ?? 0) > 0;
        }

        if (!exists)
        {
            _logger?.LogInformation("EnsureFPlateColumnAsync: ADD F_PLATE BLOB to SPR_SP_FOTO_TABLE");
            using var alter = new IBCommand(
                "ALTER TABLE SPR_SP_FOTO_TABLE ADD F_PLATE BLOB SUB_TYPE 0 SEGMENT SIZE 4096", conn);
            await alter.ExecuteNonQueryAsync(ct);
        }

        lock (SchemaLock)
        {
            EnsuredFPlate.Add(cacheKey);
        }
    }

    private async Task<bool> HasFPlateColumnAsync(IBConnection conn, CancellationToken ct)
    {
        lock (SchemaLock)
        {
            if (EnsuredFPlate.Contains(_connectionString))
                return true;
        }

        using var cmd = new IBCommand(@"
SELECT COUNT(*) FROM RDB$RELATION_FIELDS
WHERE RDB$RELATION_NAME = 'SPR_SP_FOTO_TABLE' AND RDB$FIELD_NAME = 'F_PLATE'", conn);
        var r = await cmd.ExecuteScalarAsync(ct);
        var exists = Convert.ToInt32(r ?? 0) > 0;
        if (exists)
        {
            lock (SchemaLock)
                EnsuredFPlate.Add(_connectionString);
        }
        return exists;
    }

    /// <summary>Сохранить запись о распознанном номере.</summary>
    public async Task<long> SaveRecordAsync(
        string deviceId,
        string notice,
        double? latitude,
        double? longitude,
        byte[]? screenshotBlob,
        CancellationToken ct = default,
        string? timeUtc = null,
        string? belong = null,
        byte[]? plateBlob = null)
    {
        _logger?.LogInformation(
            "SaveRecordAsync: deviceId={DeviceId}, notice={Notice}, belong={Belong}, hasCoords={HasCoords}, imageLen={ImageLen}, plateLen={PlateLen}, timeUtc={TimeUtc}",
            deviceId, notice, belong, latitude.HasValue && longitude.HasValue,
            screenshotBlob?.Length ?? 0, plateBlob?.Length ?? 0, timeUtc);
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
            // Колонку кропа готовим всегда: запись фото ниже ссылается на F_PLATE.
            await EnsureFPlateColumnAsync(conn, ct);

            using var transaction = conn.BeginTransaction();
            long newKey;
            try
            {
                newKey = await NextKeyAsync(conn, transaction, ct);
            }
            catch (Exception ex) when (
                ex.Message.Contains("is not defined", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("не определён", StringComparison.OrdinalIgnoreCase))
            {
                transaction.Rollback();
                InvalidateGeneratorCache(_connectionString, _generatorName);
                await EnsureGeneratorAsync(conn, ct);
                using var transaction2 = conn.BeginTransaction();
                newKey = await NextKeyAsync(conn, transaction2, ct);
                return await InsertRecordCoreAsync(
                    conn, transaction2, newKey, deviceId, notice, latitude, longitude,
                    screenshotBlob, plateBlob, belong, timeUtc, ct);
            }

            return await InsertRecordCoreAsync(
                conn, transaction, newKey, deviceId, notice, latitude, longitude,
                screenshotBlob, plateBlob, belong, timeUtc, ct);
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

    private async Task<long> InsertRecordCoreAsync(
        IBConnection conn,
        IBTransaction transaction,
        long newKey,
        string deviceId,
        string notice,
        double? latitude,
        double? longitude,
        byte[]? screenshotBlob,
        byte[]? plateBlob,
        string? belong,
        string? timeUtc,
        CancellationToken ct)
    {
        _logger?.LogInformation("SaveRecordAsync: newKey={NewKey} (GENERATOR)", newKey);

        var when = ResolveDateTime(timeUtc);
        var deviceIdWin = ToWin1251(deviceId ?? "NOMEROFF");
        var noticeWin = ToWin1251(notice ?? "");
        var belongWin = ToWin1251(belong ?? "");
        if (belongWin.Length > 30)
            belongWin = belongWin[..30];

        var sql = @"
            INSERT INTO SPR_SPEECH_TABLE (
                S_INCKEY, S_TYPE, S_PRELOOKED, S_DATETIME, S_NOTICE, S_DEVICEID, 
                S_CALLTYPE, S_SELSTATUS, S_BELONG
            ) VALUES (
                @S_INCKEY, @S_TYPE, @S_PRELOOKED, @S_DATETIME, @S_NOTICE, @S_DEVICEID, 
                @S_CALLTYPE, @S_SELSTATUS, @S_BELONG
            )";

        using (var command = new IBCommand(sql, conn, transaction))
        {
            command.Parameters.Add("@S_INCKEY", IBDbType.BigInt).Value = newKey;
            command.Parameters.Add("@S_TYPE", IBDbType.Integer).Value = 0;
            command.Parameters.Add("@S_PRELOOKED", IBDbType.Integer).Value = 0;
            command.Parameters.Add("@S_DEVICEID", IBDbType.VarChar).Value = deviceIdWin;
            command.Parameters.Add("@S_DATETIME", IBDbType.TimeStamp).Value = when;
            command.Parameters.Add("@S_NOTICE", IBDbType.VarChar).Value = noticeWin;
            command.Parameters.Add("@S_CALLTYPE", IBDbType.Integer).Value = 2;
            command.Parameters.Add("@S_SELSTATUS", IBDbType.SmallInt).Value = 0;
            command.Parameters.Add("@S_BELONG", IBDbType.VarChar).Value = belongWin;
            await command.ExecuteNonQueryAsync(ct);
        }

        _logger?.LogInformation("SaveRecordAsync: SPR_SPEECH_TABLE OK, S_DATETIME={When}, S_BELONG={Belong}", when, belongWin);

        if (latitude.HasValue && longitude.HasValue)
        {
            var geoSql = @"
                    INSERT INTO SPR_SP_GEO_TABLE (S_INCKEY, S_ORDER, S_LATITUDE, S_LONGITUDE)
                    VALUES (@Key, @Order, @Lat, @Lon)";
            using (var cmd = new IBCommand(geoSql, conn, transaction))
            {
                cmd.Parameters.Add("@Key", IBDbType.BigInt).Value = newKey;
                cmd.Parameters.Add("@Order", IBDbType.Integer).Value = 0;
                cmd.Parameters.Add("@Lat", IBDbType.Double).Value = latitude.Value;
                cmd.Parameters.Add("@Lon", IBDbType.Double).Value = longitude.Value;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            _logger?.LogInformation("SaveRecordAsync: SPR_SP_GEO_TABLE OK");
        }

        var hasFull = screenshotBlob != null && screenshotBlob.Length > 0;
        var hasPlate = plateBlob != null && plateBlob.Length > 0;

        // Строку в SPR_SP_FOTO_TABLE вставляем ВСЕГДА, даже если оба блоба пустые.
        // Иначе в SPR_SPEECH_TABLE запись есть, а в фотоинформации её нет вовсе —
        // именно так выглядело «нет авто в фотоинформации, а номер в БД есть».
        if (!hasFull && !hasPlate)
        {
            _logger?.LogWarning(
                "SaveRecordAsync: нет ни кадра, ни кропа номера — вставляем пустую строку фото (id={Id})",
                newKey);
        }
        else
        {
            _logger?.LogInformation(
                "SaveRecordAsync: inserting foto full={Full} plate={Plate}",
                screenshotBlob?.Length ?? 0, plateBlob?.Length ?? 0);
        }

        // InterBase 2007/2009 + InterBaseSql: несколько BLOB с @ в одном INSERT
        // уходят на сервер как есть → «Token unknown @» (−104). В Shield тот же
        // обход: ключ литералом, по одному BLOB на statement (SPR_SP_COMMENT).
        var imgSql = $"INSERT INTO SPR_SP_FOTO_TABLE (S_INCKEY, F_IMAGE) VALUES ({newKey}, @F_IMAGE)";
        using (var cmd = new IBCommand(imgSql, conn, transaction))
        {
            cmd.Parameters.Add("@F_IMAGE", IBDbType.Binary).Value =
                hasFull ? screenshotBlob! : (object)DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (await HasFPlateColumnAsync(conn, ct))
        {
            var plateSql = $"UPDATE SPR_SP_FOTO_TABLE SET F_PLATE = @F_PLATE WHERE S_INCKEY = {newKey}";
            using var cmd = new IBCommand(plateSql, conn, transaction);
            cmd.Parameters.Add("@F_PLATE", IBDbType.Binary).Value =
                hasPlate ? plateBlob! : (object)DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        _logger?.LogInformation("SaveRecordAsync: SPR_SP_FOTO_TABLE OK");

        transaction.Commit();
        _logger?.LogInformation("SaveRecordAsync: success, id={Id}", newKey);
        return newKey;
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
SELECT s.S_INCKEY, s.S_DEVICEID, s.S_DATETIME, s.S_NOTICE, s.S_BELONG, g.S_LATITUDE, g.S_LONGITUDE
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
                        S_BELONG = reader["S_BELONG"] is DBNull ? "" : (reader["S_BELONG"]?.ToString() ?? ""),
                        S_LATITUDE = reader["S_LATITUDE"] is DBNull ? null : Convert.ToDouble(reader["S_LATITUDE"]),
                        S_LONGITUDE = reader["S_LONGITUDE"] is DBNull ? null : Convert.ToDouble(reader["S_LONGITUDE"]),
                        HasFoto = false,
                        HasPlate = false
                    });
                }
            }

            // Флаги наличия полного кадра / кропа номера (без чтения самих BLOB)
            if (result.Count > 0)
            {
                var hasPlateCol = await HasFPlateColumnAsync(conn, ct);
                var byId = result.ToDictionary(r => r.S_INCKEY);
                var ids = result.Select(r => r.S_INCKEY).ToList();
                for (var i = 0; i < ids.Count; i += inChunkSize)
                {
                    var chunk = ids.Skip(i).Take(inChunkSize).ToList();
                    var inClause = string.Join(", ", chunk);
                    using (var fotoCmd = new IBCommand(
                               $"SELECT S_INCKEY FROM SPR_SP_FOTO_TABLE WHERE S_INCKEY IN ({inClause}) AND F_IMAGE IS NOT NULL",
                               conn))
                    {
                        await using var fotoReader = await fotoCmd.ExecuteReaderAsync(ct);
                        while (await fotoReader.ReadAsync(ct))
                        {
                            var key = Convert.ToInt64(fotoReader["S_INCKEY"]);
                            if (byId.TryGetValue(key, out var row))
                                row.HasFoto = true;
                        }
                    }

                    if (hasPlateCol)
                    {
                        using var plateCmd = new IBCommand(
                            $"SELECT S_INCKEY FROM SPR_SP_FOTO_TABLE WHERE S_INCKEY IN ({inClause}) AND F_PLATE IS NOT NULL",
                            conn);
                        await using var plateReader = await plateCmd.ExecuteReaderAsync(ct);
                        while (await plateReader.ReadAsync(ct))
                        {
                            var key = Convert.ToInt64(plateReader["S_INCKEY"]);
                            if (byId.TryGetValue(key, out var row))
                                row.HasPlate = true;
                        }
                    }
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
        => await GetFotoBlobAsync(sInckey, plate: false, ct);

    public async Task<byte[]?> GetPlateImageAsync(long sInckey, CancellationToken ct = default)
        => await GetFotoBlobAsync(sInckey, plate: true, ct);

    private async Task<byte[]?> GetFotoBlobAsync(long sInckey, bool plate, CancellationToken ct)
    {
        _logger?.LogInformation("GetFotoBlobAsync: sInckey={Key}, plate={Plate}", sInckey, plate);
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _logger?.LogWarning("GetFotoBlobAsync: Interbase не настроен.");
            return null;
        }

        IBConnection? conn = null;
        try
        {
            conn = GetConnection(_connectionString);
            if (plate && !await HasFPlateColumnAsync(conn, ct))
                return null;

            var col = plate ? "F_PLATE" : "F_IMAGE";
            var sql = $"SELECT {col} FROM SPR_SP_FOTO_TABLE WHERE S_INCKEY = @Key";
            using (var cmd = new IBCommand(sql, conn))
            {
                cmd.Parameters.Add("@Key", IBDbType.BigInt).Value = sInckey;
                var r = await cmd.ExecuteScalarAsync(ct);
                if (r is byte[] bytes && bytes.Length > 0)
                {
                    _logger?.LogInformation("GetFotoBlobAsync: получено {Len} байт ({Col})", bytes.Length, col);
                    return bytes;
                }
            }
            _logger?.LogInformation("GetFotoBlobAsync: изображение не найдено ({Col})", col);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "GetFotoBlobAsync: ошибка");
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
    /// <summary>Уверенность OCR, например «87%».</summary>
    public string S_BELONG { get; set; } = "";
    public double? S_LATITUDE { get; set; }
    public double? S_LONGITUDE { get; set; }
    public bool HasFoto { get; set; }
    public bool HasPlate { get; set; }
}
