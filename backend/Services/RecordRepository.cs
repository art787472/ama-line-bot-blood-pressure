using BloodPressureBot.Models;
using Npgsql;

namespace BloodPressureBot.Services;

public class RecordRepository
{
    private readonly string _connectionString;

    public RecordRepository(IConfiguration config)
    {
        var cs = config["Database:ConnectionString"];
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                "Database:ConnectionString is not configured. " +
                "Set it via `dotnet user-secrets set \"Database:ConnectionString\" \"...\"`.");
        _connectionString = cs;
    }

    public async Task EnsureSchemaAsync()
    {
        const string sql = """
            create table if not exists blood_pressure_records (
                id uuid primary key default gen_random_uuid(),
                user_id text not null,
                systolic int not null,
                diastolic int not null,
                pulse int,
                measured_at timestamptz not null default now(),
                created_at timestamptz not null default now()
            );
            create index if not exists idx_records_user_measured
                on blood_pressure_records(user_id, measured_at desc);
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<BloodPressureRecord> InsertAsync(string userId, BloodPressureReading reading, DateTime measuredAt)
    {
        const string sql = """
            insert into blood_pressure_records (user_id, systolic, diastolic, pulse, measured_at)
            values (@user_id, @systolic, @diastolic, @pulse, @measured_at)
            returning id, created_at;
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("user_id", userId);
        cmd.Parameters.AddWithValue("systolic", reading.Systolic);
        cmd.Parameters.AddWithValue("diastolic", reading.Diastolic);
        cmd.Parameters.AddWithValue("pulse", (object?)reading.Pulse ?? DBNull.Value);
        cmd.Parameters.AddWithValue("measured_at", measuredAt);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new BloodPressureRecord(
            reader.GetGuid(0), userId, reading.Systolic, reading.Diastolic,
            reading.Pulse, measuredAt, reader.GetDateTime(1));
    }

    public async Task<List<BloodPressureRecord>> GetRecordsAsync(string? userId, int limit = 500)
    {
        var sql = """
            select id, user_id, systolic, diastolic, pulse, measured_at, created_at
            from blood_pressure_records
            """ + (string.IsNullOrEmpty(userId) ? "" : " where user_id = @user_id ")
                + " order by measured_at desc limit @limit";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (!string.IsNullOrEmpty(userId))
            cmd.Parameters.AddWithValue("user_id", userId);
        cmd.Parameters.AddWithValue("limit", limit);

        var results = new List<BloodPressureRecord>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new BloodPressureRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.GetDateTime(5),
                reader.GetDateTime(6)));
        }
        return results;
    }

    public async Task<Stats> GetStatsAsync(string? userId, int days = 30)
    {
        var sql = """
            select count(*), avg(systolic), avg(diastolic), avg(pulse), max(systolic), min(systolic)
            from blood_pressure_records
            where measured_at >= now() - (@days || ' days')::interval
            """ + (string.IsNullOrEmpty(userId) ? "" : " and user_id = @user_id");

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("days", days);
        if (!string.IsNullOrEmpty(userId))
            cmd.Parameters.AddWithValue("user_id", userId);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new Stats(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : (double)reader.GetDecimal(1),
            reader.IsDBNull(2) ? null : (double)reader.GetDecimal(2),
            reader.IsDBNull(3) ? null : (double)reader.GetDecimal(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5));
    }
}
