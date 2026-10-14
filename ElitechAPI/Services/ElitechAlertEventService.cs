// Services/ElitechAlertEventService.cs
using Elitech.Data;
using Elitech.Models.Alerts;
using MongoDB.Driver;

namespace Elitech.Services;

public class ElitechAlertEventService
{
    private readonly IMongoCollection<ElitechAlertEvent> _events;
    private readonly IMongoCollection<ElitechAlertState> _states;

    public ElitechAlertEventService(MongoContext ctx)
    {
        _events = ctx.Database.GetCollection<ElitechAlertEvent>("AlertEvents");
        _states = ctx.Database.GetCollection<ElitechAlertState>("AlertStates");
        EnsureIndexes();
    }

    private void EnsureIndexes()
    {
        _events.Indexes.CreateOne(new CreateIndexModel<ElitechAlertEvent>(
            Builders<ElitechAlertEvent>.IndexKeys
                .Ascending(x => x.UserId)
                .Descending(x => x.OccurredAtUtc),
            new CreateIndexOptions
            {
                Name = "ix_event_user_time"
            }));

        _events.Indexes.CreateOne(new CreateIndexModel<ElitechAlertEvent>(
            Builders<ElitechAlertEvent>.IndexKeys
                .Ascending(x => x.DeviceGuid)
                .Descending(x => x.OccurredAtUtc),
            new CreateIndexOptions
            {
                Name = "ix_event_device_time"
            }));

        // Tự xóa AlertEvents sau 61 ngày
        _events.Indexes.CreateOne(new CreateIndexModel<ElitechAlertEvent>(
            Builders<ElitechAlertEvent>.IndexKeys
                .Ascending(x => x.OccurredAtUtc),
            new CreateIndexOptions
            {
                Name = "ttl_event_61days",
                ExpireAfter = TimeSpan.FromDays(61)
            }));

        _states.Indexes.CreateOne(new CreateIndexModel<ElitechAlertState>(
            Builders<ElitechAlertState>.IndexKeys
                .Ascending(x => x.UserId)
                .Ascending(x => x.DeviceGuid),
            new CreateIndexOptions
            {
                Unique = true,
                Name = "ux_state_user_device"
            }));
    }

    public async Task<ElitechAlertState?> GetStateAsync(
        string userId,
        string deviceGuid,
        CancellationToken ct = default)
    {
        return await _states
            .Find(x => x.UserId == userId && x.DeviceGuid == deviceGuid)
            .FirstOrDefaultAsync(ct);
    }

    public Task UpsertStateAsync(
        ElitechAlertState s,
        CancellationToken ct = default)
    {
        s.UpdatedAtUtc = DateTime.UtcNow;

        var filter =
            Builders<ElitechAlertState>.Filter.Where(
                x => x.UserId == s.UserId &&
                     x.DeviceGuid == s.DeviceGuid);

        var update =
            Builders<ElitechAlertState>.Update

                .Set(
                    x => x.ConsecutiveBadHits,
                    s.ConsecutiveBadHits)

                .Set(
                    x => x.IsBad,
                    s.IsBad)

                .Set(
                    x => x.LastAlertAtUtc,
                    s.LastAlertAtUtc)

                // QUAN TRỌNG:
                // lưu mốc SMS để cooldown 5 phút không bị mất
                .Set(
                    x => x.LastSmsAtUtc,
                    s.LastSmsAtUtc)

                .Set(
                    x => x.LastReasons,
                    s.LastReasons)

                .Set(
                    x => x.UpdatedAtUtc,
                    s.UpdatedAtUtc)

                .Set(
                    x => x.LastSampleTs,
                    s.LastSampleTs)

                .SetOnInsert(
                    x => x.UserId,
                    s.UserId)

                .SetOnInsert(
                    x => x.DeviceGuid,
                    s.DeviceGuid);

        return _states.UpdateOneAsync(
            filter,
            update,
            new UpdateOptions
            {
                IsUpsert = true
            },
            ct);
    }

    public Task InsertEventAsync(
        ElitechAlertEvent e,
        CancellationToken ct = default)
    {
        return _events.InsertOneAsync(
            e,
            cancellationToken: ct);
    }

    // =========================================================
    // QUERY ALERT EVENTS
    // Controller ElitechAlertEventsController đang dùng method này.
    // Không được xóa.
    // =========================================================
    public async Task<(List<ElitechAlertEvent> items, long total)> QueryAsync(
        string? userId,
        bool isAdmin,
        string? deviceGuid,
        DateTime? fromUtc,
        DateTime? toUtc,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 200);

        var fb =
            Builders<ElitechAlertEvent>.Filter;

        var filter =
            fb.Empty;

        // User thường chỉ được đọc event của chính mình.
        // Admin được đọc toàn bộ.
        if (!isAdmin)
        {
            filter &=
                fb.Eq(
                    x => x.UserId,
                    userId);
        }

        if (!string.IsNullOrWhiteSpace(deviceGuid))
        {
            filter &=
                fb.Eq(
                    x => x.DeviceGuid,
                    deviceGuid.Trim());
        }

        if (fromUtc.HasValue)
        {
            filter &=
                fb.Gte(
                    x => x.OccurredAtUtc,
                    fromUtc.Value);
        }

        if (toUtc.HasValue)
        {
            filter &=
                fb.Lte(
                    x => x.OccurredAtUtc,
                    toUtc.Value);
        }

        var find =
            _events
                .Find(filter)
                .SortByDescending(
                    x => x.OccurredAtUtc);

        var total =
            await find.CountDocumentsAsync(ct);

        var items =
            await find
                .Skip((page - 1) * pageSize)
                .Limit(pageSize)
                .ToListAsync(ct);

        return (items, total);
    }
}
