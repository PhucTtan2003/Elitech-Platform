using System.Globalization;
using Elitech.Data;
using Elitech.Models.Sms;
using MongoDB.Driver;

namespace Elitech.Services.SmsSchedule;

public class SmsTemperatureScheduleService
{
    private readonly IMongoCollection<SmsTemperatureSchedule> _schedules;
    private readonly IMongoCollection<SmsTemperatureSendLog> _logs;

    public SmsTemperatureScheduleService(MongoContext ctx)
    {
        _schedules = ctx.Database.GetCollection<SmsTemperatureSchedule>("SmsTemperatureSchedules");
        _logs = ctx.Database.GetCollection<SmsTemperatureSendLog>("SmsTemperatureSendLogs");

        EnsureIndexes();
    }

    private void EnsureIndexes()
    {
        _schedules.Indexes.CreateOne(new CreateIndexModel<SmsTemperatureSchedule>(
            Builders<SmsTemperatureSchedule>.IndexKeys
                .Ascending(x => x.UserId)
                .Ascending(x => x.DeviceGuid)
                .Ascending(x => x.Phone),
            new CreateIndexOptions
            {
                Unique = true,
                Name = "ux_user_device_phone"
            }));

        _schedules.Indexes.CreateOne(new CreateIndexModel<SmsTemperatureSchedule>(
            Builders<SmsTemperatureSchedule>.IndexKeys
                .Ascending(x => x.Enabled)
                .Ascending(x => x.ScheduleTimes),
            new CreateIndexOptions
            {
                Name = "ix_enabled_times"
            }));

        _logs.Indexes.CreateOne(new CreateIndexModel<SmsTemperatureSendLog>(
            Builders<SmsTemperatureSendLog>.IndexKeys
                .Descending(x => x.CreatedAtUtc),
            new CreateIndexOptions
            {
                Name = "ix_log_time"
            }));
    }

    public async Task<List<SmsTemperatureSchedule>> GetByUserAsync(string userId, CancellationToken ct = default)
    {
        return await _schedules
            .Find(x => x.UserId == userId)
            .SortByDescending(x => x.UpdatedAtUtc)
            .ToListAsync(ct);
    }

    public async Task<SmsTemperatureSchedule?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return await _schedules.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
    }

    public async Task<SmsTemperatureSchedule> UpsertAsync(
        string userId,
        string deviceGuid,
        string? deviceName,
        string phone,
        IEnumerable<string> scheduleTimes,
        bool enabled,
        CancellationToken ct = default)
    {
        deviceGuid = NormalizeGuid(deviceGuid);
        phone = NormalizePhone(phone);

        var times = NormalizeTimes(scheduleTimes);

        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("userId required");

        if (string.IsNullOrWhiteSpace(deviceGuid))
            throw new ArgumentException("deviceGuid required");

        if (string.IsNullOrWhiteSpace(phone))
            throw new ArgumentException("phone required");

        if (times.Count == 0)
            throw new ArgumentException("scheduleTimes required, example: 07:00, 15:00");

        var now = DateTime.UtcNow;

        var filter = Builders<SmsTemperatureSchedule>.Filter.Where(x =>
            x.UserId == userId &&
            x.DeviceGuid == deviceGuid &&
            x.Phone == phone);

        var update = Builders<SmsTemperatureSchedule>.Update
            .SetOnInsert(x => x.UserId, userId)
            .SetOnInsert(x => x.DeviceGuid, deviceGuid)
            .SetOnInsert(x => x.Phone, phone)
            .SetOnInsert(x => x.CreatedAtUtc, now)
            .Set(x => x.DeviceName, deviceName)
            .Set(x => x.ScheduleTimes, times)
            .Set(x => x.Enabled, enabled)
            .Set(x => x.TimeZoneId, "Asia/Ho_Chi_Minh")
            .Set(x => x.UpdatedAtUtc, now);

        await _schedules.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);

        return await _schedules.Find(filter).FirstAsync(ct);
    }

    public async Task<bool> DeleteAsync(string id, string userId, bool isAdmin, CancellationToken ct = default)
    {
        var filter = isAdmin
            ? Builders<SmsTemperatureSchedule>.Filter.Eq(x => x.Id, id)
            : Builders<SmsTemperatureSchedule>.Filter.Where(x => x.Id == id && x.UserId == userId);

        var res = await _schedules.DeleteOneAsync(filter, ct);
        return res.DeletedCount > 0;
    }

    public async Task<List<SmsTemperatureSchedule>> GetDueCandidatesAsync(string hhmm, CancellationToken ct = default)
    {
        var filter = Builders<SmsTemperatureSchedule>.Filter.And(
            Builders<SmsTemperatureSchedule>.Filter.Eq(x => x.Enabled, true),
            Builders<SmsTemperatureSchedule>.Filter.AnyEq(x => x.ScheduleTimes, hhmm)
        );

        return await _schedules.Find(filter).ToListAsync(ct);
    }

    public async Task<bool> TryClaimAsync(string scheduleId, string dueKey, CancellationToken ct = default)
    {
        var filter = Builders<SmsTemperatureSchedule>.Filter.And(
            Builders<SmsTemperatureSchedule>.Filter.Eq(x => x.Id, scheduleId),
            Builders<SmsTemperatureSchedule>.Filter.Eq(x => x.Enabled, true),
            Builders<SmsTemperatureSchedule>.Filter.Not(
                Builders<SmsTemperatureSchedule>.Filter.AnyEq(x => x.ClaimedKeys, dueKey)),
            Builders<SmsTemperatureSchedule>.Filter.Not(
                Builders<SmsTemperatureSchedule>.Filter.AnyEq(x => x.SentKeys, dueKey))
        );

        var update = Builders<SmsTemperatureSchedule>.Update
            .AddToSet(x => x.ClaimedKeys, dueKey)
            .Set(x => x.LastClaimedAtUtc, DateTime.UtcNow)
            .Set(x => x.UpdatedAtUtc, DateTime.UtcNow);

        var res = await _schedules.UpdateOneAsync(filter, update, cancellationToken: ct);
        return res.ModifiedCount == 1;
    }

    public async Task MarkSentAsync(string scheduleId, string dueKey, CancellationToken ct = default)
    {
        await _schedules.UpdateOneAsync(
            x => x.Id == scheduleId,
            Builders<SmsTemperatureSchedule>.Update
                .AddToSet(x => x.SentKeys, dueKey)
                .Set(x => x.LastSentAtUtc, DateTime.UtcNow)
                .Set(x => x.UpdatedAtUtc, DateTime.UtcNow),
            cancellationToken: ct);
    }

    public Task InsertLogAsync(SmsTemperatureSendLog log, CancellationToken ct = default)
    {
        log.CreatedAtUtc = DateTime.UtcNow;
        return _logs.InsertOneAsync(log, cancellationToken: ct);
    }

    public static string NormalizeGuid(string? s)
        => string.IsNullOrWhiteSpace(s) ? "" : s.Trim().ToUpperInvariant();

    public static string NormalizePhone(string? phone)
    {
        var s = (phone ?? "").Trim().Replace(" ", "").Replace(".", "").Replace("-", "");
        return s;
    }

    public static List<string> NormalizeTimes(IEnumerable<string> times)
    {
        var result = new List<string>();

        foreach (var raw in times ?? Array.Empty<string>())
        {
            var s = (raw ?? "").Trim();

            if (TimeSpan.TryParseExact(s, @"hh\:mm", CultureInfo.InvariantCulture, out var t) ||
                TimeSpan.TryParseExact(s, @"h\:mm", CultureInfo.InvariantCulture, out t))
            {
                result.Add($"{t.Hours:00}:{t.Minutes:00}");
            }
        }

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();
    }
}