using InterBaseSql.Data.InterBaseClient;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Data.Common;
using System.Text;
using System.Transactions;

namespace Nomeroff.Interbase.Api.Interbase;

/// <summary>
/// Сервис для записи распознанных номеров в Interbase.
///
/// Структура таблиц:
///
/// SPR_SPEECH_TABLE — основная таблица сеансов (регистраций):
///   S_INCKEY      BIGINT      — идентификатор (первичный ключ)
///   S_DEVICEID    VARCHAR     — имя устройства
///   S_DATETIME    TIMESTAMP   — дата регистрации
///   S_NOTICE      VARCHAR     — номер автомобиля
///   S_TYPE, S_PRELOOKED и др. — дополнительные поля
///
/// SPR_SP_GEO_TABLE — геоданные сеанса (GPS):
///   S_INCKEY      BIGINT      — ссылка на SPR_SPEECH_TABLE
///   S_LATITUDE    DOUBLE PRECISION — широта (X), градусы
///   S_LONGITUDE   DOUBLE PRECISION — долгота (Y), градусы
///
/// SPR_SP_FOTO_TABLE — фотоснимки сеанса (скриншот при распознавании):
///   S_INCKEY      BIGINT      — ссылка на SPR_SPEECH_TABLE
///   F_IMAGE       BLOB        — снимок (изображение)
/// </summary>
public class NomeroffInterbaseService
{
    private readonly string _connectionString;
    private readonly ILogger? _logger;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_connectionString);

    public NomeroffInterbaseService(string? connectionString, ILogger? logger = null)
    {
        _connectionString = connectionString ?? "";
        _logger = logger;
    }

    /// <summary>Создать экземпляр с указанной строкой подключения.</summary>
    public NomeroffInterbaseService WithConnection(string connectionString) =>
        new NomeroffInterbaseService(connectionString, _logger);

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

    /// <summary>Сохранить запись о распознанном номере.</summary>
    /// <param name="deviceId">Имя устройства (S_DEVICEID)</param>
    /// <param name="notice">Номер автомобиля (S_NOTICE)</param>
    /// <param name="latitude">Широта (S_LATITUDE) или null</param>
    /// <param name="longitude">Долгота (S_LONGITUDE) или null</param>
    /// <param name="screenshotBlob">Скриншот (F_IMAGE) или null</param>
    public async Task<long> SaveRecordAsync(
        string deviceId,
        string notice,
        double? latitude,
        double? longitude,
        byte[]? screenshotBlob,
        CancellationToken ct = default)
    {
        _logger?.LogInformation("SaveRecordAsync: deviceId={DeviceId}, notice={Notice}, hasCoords={HasCoords}, imageLen={ImageLen}",
            deviceId, notice, latitude.HasValue && longitude.HasValue, screenshotBlob?.Length ?? 0);
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _logger?.LogWarning("SaveRecordAsync: Interbase не настроен.");
            throw new InvalidOperationException("Interbase не настроен.");
        }

        IBConnection? conn = null;
        try
        {
            conn = GetConnection(_connectionString);
            long newKey;
            var maxKeySql = "SELECT MAX(S_INCKEY) FROM SPR_SPEECH_TABLE";

            using (var transaction = conn.BeginTransaction())
            {
                using (var command = new IBCommand(maxKeySql, conn, transaction))
                {
                    var maxKey = await command.ExecuteScalarAsync(ct);
                    long maxKeyLong;
                    if (maxKey == null || maxKey == DBNull.Value)
                    {
                        maxKeyLong = 0;
                    }
                    else
                    {
                        maxKeyLong = Convert.ToInt64(maxKey);
                    }
                    newKey = maxKeyLong + 1;
                }
                _logger?.LogInformation("SaveRecordAsync: newKey={NewKey} (MAX+1)", newKey);

                var now = DateTime.Now;
                var deviceIdWin = ToWin1251(deviceId ?? "NOMEROFF");
                var noticeWin = ToWin1251(notice ?? "");
                _logger?.LogDebug("SaveRecordAsync: deviceIdWin len={Len}, noticeWin len={NoticeLen}", deviceIdWin.Length, noticeWin.Length);
                // 2. INSERT в SPR_SPEECH_TABLE (минимальный набор полей для Nomeroff)
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
                    command.Parameters.AddWithValue("@S_DATETIME", IBDbType.TimeStamp).Value = now;
                    command.Parameters.AddWithValue("@S_NOTICE", IBDbType.VarChar).Value = noticeWin;
                    command.Parameters.AddWithValue("@S_CALLTYPE", IBDbType.Integer).Value = 2;
                    command.Parameters.AddWithValue("@S_SELSTATUS", IBDbType.SmallInt).Value = 0;

                    await command.ExecuteNonQueryAsync(ct);
                }

                _logger?.LogInformation("SaveRecordAsync: SPR_SPEECH_TABLE OK");

                // 3. INSERT в SPR_SP_GEO_TABLE если есть координаты
                if (latitude.HasValue && longitude.HasValue)
                {
                    var geoSql = @"
                    INSERT INTO SPR_SP_GEO_TABLE (S_INCKEY, S_ORDER, S_LATITUDE, S_LONGITUDE)
                    VALUES (@Key, @Order, @Lat, @Lon)";
                    using (var cmd = new IBCommand(geoSql, conn, transaction))
                    {
                        cmd.Parameters.Add("@Key", IBDbType.BigInt).Value = newKey;
                        cmd.Parameters.AddWithValue("@Order", IBDbType.Integer).Value = 0; // !!! Обязательный параметр - без него ошибка записи
                        cmd.Parameters.AddWithValue("@Lat", IBDbType.Double).Value = latitude.Value;
                        cmd.Parameters.AddWithValue("@Lon", IBDbType.Double).Value = longitude.Value;
                        await cmd.ExecuteNonQueryAsync(ct);
                    }
                    _logger?.LogInformation("SaveRecordAsync: SPR_SP_GEO_TABLE OK");
                }

                // 4. INSERT в SPR_SP_FOTO_TABLE если есть скриншот (независимо от наличия GPS)
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

    /// <summary>Получить записи из SPR_SPEECH_TABLE с GEO (пагинация в C#).</summary>
    public async Task<IReadOnlyList<DbRecordDto>> GetRecordsAsync(int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        _logger?.LogInformation("GetRecordsAsync: limit={Limit}, offset={Offset} (C# pagination)", limit, offset);
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _logger?.LogWarning("GetRecordsAsync: Interbase не настроен.");
            return Array.Empty<DbRecordDto>();
        }

        // Ограничиваем лимит, чтобы не перегрузить память
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);

        IBConnection? conn = null;
        try
        {
            conn = GetConnection(_connectionString);

            // 🔹 Шаг 1: Получаем ВСЕ S_INCKEY в нужном порядке (только ID, это быстро)
            var allIds = new List<long>();
            var sqlIds = "SELECT s.S_INCKEY FROM SPR_SPEECH_TABLE s ORDER BY s.S_INCKEY DESC";

            using (var cmd = new IBCommand(sqlIds, conn))
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    allIds.Add(Convert.ToInt64(reader["S_INCKEY"]));
                }
            }

            // 🔹 Шаг 2: Применяем пагинацию в C#
            var pageIds = allIds.Skip(offset).Take(limit).ToList();

            if (pageIds.Count == 0)
            {
                _logger?.LogInformation("GetRecordsAsync: нет записей для этой страницы");
                return Array.Empty<DbRecordDto>();
            }

            _logger?.LogDebug("GetRecordsAsync: выбрано {Count} ID для загрузки деталей", pageIds.Count);

            // 🔹 Шаг 3: Загружаем полные данные только для нужных ID
            // InterBase 2009 может иметь лимит на количество элементов в IN (...), поэтому разбиваем на чанки
            const int inChunkSize = 100;
            var result = new List<DbRecordDto>();

            for (int i = 0; i < pageIds.Count; i += inChunkSize)
            {
                var chunk = pageIds.Skip(i).Take(inChunkSize).ToList();
                var inClause = string.Join(", ", chunk);

                var sqlData = $@"
                SELECT s.S_INCKEY, s.S_DEVICEID, s.S_DATETIME, s.S_NOTICE, g.S_LATITUDE, g.S_LONGITUDE
                FROM SPR_SPEECH_TABLE s
                LEFT JOIN SPR_SP_GEO_TABLE g ON g.S_INCKEY = s.S_INCKEY
                WHERE s.S_INCKEY IN ({inClause})
                ORDER BY s.S_INCKEY DESC";

                using (var cmd = new IBCommand(sqlData, conn))
                {
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
                            HasFoto = false // При необходимости можно добавить проверку SPR_SP_FOTO_TABLE
                        });
                    }
                }
            }

            // 🔹 Шаг 4: Восстанавливаем порядок (на случай, если IN (...) вернул не в том порядке)
            var orderedResult = result
                .OrderByDescending(r => r.S_INCKEY)
                .ToList();

            _logger?.LogInformation("GetRecordsAsync: получено {Count} записей", orderedResult.Count);
            return orderedResult;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "GetRecordsAsync: ошибка");
            return Array.Empty<DbRecordDto>();
        }
        finally
        {
            CloseConnection(conn);
        }
    }

    /// <summary>Получить изображение (F_IMAGE) из SPR_SP_FOTO_TABLE по S_INCKEY.</summary>
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

    /// <summary>Удалить запись по S_INCKEY (из всех связанных таблиц).</summary>
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
                _logger?.LogDebug("DeleteRecordAsync: {Table} OK", table);
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
            using (var cmd = new IBCommand("SELECT MAX(S_INCKEY) FROM SPR_SPEECH_TABLE", conn))
            {
                var r = await cmd.ExecuteScalarAsync(ct);
                var maxKey = r != null && r != DBNull.Value ? Convert.ToInt64(r) : 0;
                _logger?.LogInformation("TestConnectionAsync: OK, MAX(S_INCKEY)={Max}", maxKey);
                return (true, $"Подключение OK. MAX(S_INCKEY) = {maxKey}");
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

/// <summary>DTO записи из БД для просмотрщика.</summary>
public class DbRecordDto
{
    public long S_INCKEY { get; set; }
    public string S_DEVICEID { get; set; } = "";
    public DateTime? S_DATETIME { get; set; }
    public string S_NOTICE { get; set; } = "";
    public double? S_LATITUDE { get; set; }
    public double? S_LONGITUDE { get; set; }
    /// <summary>Есть ли скриншот в SPR_SP_FOTO_TABLE.</summary>
    public bool HasFoto { get; set; }
}
