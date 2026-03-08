using InterBaseSql.Data.InterBaseClient;
using System.Data;
using System.Text;

namespace Nomeroff.Interbase.Api.Interbase;

/// <summary>Сервис для записи распознанных номеров в Interbase (SPR_SPEECH_TABLE, SPR_SP_GEO_TABLE, SPR_SP_FOTO_TABLE).</summary>
public class NomeroffInterbaseService
{
    private readonly string _connectionString;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_connectionString);

    public NomeroffInterbaseService(string connectionString)
    {
        _connectionString = connectionString ?? "";
    }

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
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("Interbase не настроен.");

        IBConnection? conn = null;
        try
        {
            conn = GetConnection(_connectionString);
            long newKey;

            // 1. Получить следующий S_INCKEY
            using (var cmdMax = new IBCommand("SELECT COALESCE(MAX(S_INCKEY), 0) + 1 FROM SPR_SPEECH_TABLE", conn))
            {
                var r = await cmdMax.ExecuteScalarAsync(ct);
                newKey = Convert.ToInt64(r);
            }

            var now = DateTime.Now;
            var deviceIdWin = ToWin1251(deviceId ?? "NOMEROFF");
            var noticeWin = ToWin1251(notice ?? "");

            // 2. INSERT в SPR_SPEECH_TABLE (минимальный набор полей для Nomeroff)
            var speechSql = @"
                INSERT INTO SPR_SPEECH_TABLE (S_INCKEY, S_TYPE, S_PRELOOKED, S_DEVICEID, S_DATETIME, S_NOTICE)
                VALUES (@Key, 0, 0, @DeviceId, @DateTime, @Notice)";
            using (var cmd = new IBCommand(speechSql, conn))
            {
                cmd.Parameters.Add("@Key", IBDbType.BigInt).Value = newKey;
                cmd.Parameters.Add("@DeviceId", IBDbType.VarChar).Value = deviceIdWin;
                cmd.Parameters.Add("@DateTime", IBDbType.TimeStamp).Value = now;
                cmd.Parameters.Add("@Notice", IBDbType.VarChar).Value = noticeWin;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // 3. INSERT в SPR_SP_GEO_TABLE если есть координаты
            if (latitude.HasValue && longitude.HasValue)
            {
                var geoSql = @"
                    INSERT INTO SPR_SP_GEO_TABLE (S_INCKEY, S_LATITUDE, S_LONGITUDE)
                    VALUES (@Key, @Lat, @Lon)";
                using (var cmd = new IBCommand(geoSql, conn))
                {
                    cmd.Parameters.Add("@Key", IBDbType.BigInt).Value = newKey;
                    cmd.Parameters.Add("@Lat", IBDbType.Double).Value = latitude.Value;
                    cmd.Parameters.Add("@Lon", IBDbType.Double).Value = longitude.Value;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }

            // 4. INSERT в SPR_SP_FOTO_TABLE если есть скриншот
            if (screenshotBlob != null && screenshotBlob.Length > 0)
            {
                var fotoSql = @"
                    INSERT INTO SPR_SP_FOTO_TABLE (S_INCKEY, F_IMAGE)
                    VALUES (@Key, @Image)";
                using (var cmd = new IBCommand(fotoSql, conn))
                {
                    cmd.Parameters.Add("@Key", IBDbType.BigInt).Value = newKey;
                    cmd.Parameters.Add("@Image", IBDbType.Binary).Value = screenshotBlob;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }

            return newKey;
        }
        finally
        {
            CloseConnection(conn);
        }
    }

    /// <summary>Получить записи из SPR_SPEECH_TABLE с GEO (пагинация).</summary>
    public async Task<IReadOnlyList<DbRecordDto>> GetRecordsAsync(int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            return Array.Empty<DbRecordDto>();

        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Max(0, offset);

        IBConnection? conn = null;
        try
        {
            conn = GetConnection(_connectionString);
            var sql = @"
                SELECT FIRST @Limit SKIP @Offset
                s.S_INCKEY, s.S_DEVICEID, s.S_DATETIME, s.S_NOTICE, g.S_LATITUDE, g.S_LONGITUDE
                FROM SPR_SPEECH_TABLE s
                LEFT JOIN SPR_SP_GEO_TABLE g ON g.S_INCKEY = s.S_INCKEY
                ORDER BY s.S_INCKEY DESC";
            var list = new List<DbRecordDto>();
            using (var cmd = new IBCommand(sql, conn))
            {
                cmd.Parameters.Add("@Limit", IBDbType.Integer).Value = limit;
                cmd.Parameters.Add("@Offset", IBDbType.Integer).Value = offset;
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    list.Add(new DbRecordDto
                    {
                        S_INCKEY = Convert.ToInt64(r["S_INCKEY"]),
                        S_DEVICEID = r["S_DEVICEID"]?.ToString() ?? "",
                        S_DATETIME = r["S_DATETIME"] is DBNull ? null : Convert.ToDateTime(r["S_DATETIME"]),
                        S_NOTICE = r["S_NOTICE"]?.ToString() ?? "",
                        S_LATITUDE = r["S_LATITUDE"] is DBNull ? null : Convert.ToDouble(r["S_LATITUDE"]),
                        S_LONGITUDE = r["S_LONGITUDE"] is DBNull ? null : Convert.ToDouble(r["S_LONGITUDE"])
                    });
                }
            }
            return list;
        }
        catch
        {
            return Array.Empty<DbRecordDto>();
        }
        finally
        {
            CloseConnection(conn);
        }
    }

    public async Task<(bool success, string message)> TestConnectionAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            return (false, "Interbase не настроен.");

        IBConnection? conn = null;
        try
        {
            conn = GetConnection(_connectionString);
            using (var cmd = new IBCommand("SELECT MAX(S_INCKEY) FROM SPR_SPEECH_TABLE", conn))
            {
                var r = await cmd.ExecuteScalarAsync(ct);
                var maxKey = r != null && r != DBNull.Value ? Convert.ToInt64(r) : 0;
                return (true, $"Подключение OK. MAX(S_INCKEY) = {maxKey}");
            }
        }
        catch (Exception ex)
        {
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
}
