using Elitech.Data;
using Elitech.Models;
using MongoDB.Driver;

namespace Elitech.Services;

public class ElitechDeviceAssignmentService
{
    private readonly IMongoCollection<ElitechDeviceAssignment> _col;

    public ElitechDeviceAssignmentService(MongoContext ctx)
    {
        _col = ctx.Database.GetCollection<ElitechDeviceAssignment>("DeviceAssignments");
        EnsureIndexes();
    }

    // ✅ Thống nhất chuẩn guid: TRIM + UPPER (đồng bộ với Controller/RuleService/Worker)
    private static string NormalizeGuid(string? s)
        => string.IsNullOrWhiteSpace(s) ? "" : s.Trim().ToUpperInvariant();

    // ✅ Public helper để Controller/Service khác dùng chung (khỏi copy normalize)
    public static string NormalizeGuidPublic(string? s) => NormalizeGuid(s);

    private void EnsureIndexes()
    {
        var keys = Builders<ElitechDeviceAssignment>.IndexKeys
            .Ascending(x => x.UserId)
            .Ascending(x => x.DeviceGuid);

        _col.Indexes.CreateOne(new CreateIndexModel<ElitechDeviceAssignment>(
            keys,
            new CreateIndexOptions { Unique = true, Name = "ux_user_device" }));

        _col.Indexes.CreateOne(new CreateIndexModel<ElitechDeviceAssignment>(
            Builders<ElitechDeviceAssignment>.IndexKeys.Ascending(x => x.UserId),
            new CreateIndexOptions { Name = "ix_user" }));
    }

    // ✅ đảm bảo trả về DeviceGuid đã normalize (tránh lệch do DB lưu raw)
    public async Task<List<ElitechDeviceAssignment>> GetByUserAsync(string userId, CancellationToken ct = default)
    {
        var list = await _col.Find(x => x.UserId == userId)
            .SortBy(x => x.DeviceGuid)
            .ToListAsync(ct);

        // normalize in-memory để chỗ khác dùng luôn an toàn
        foreach (var a in list)
            a.DeviceGuid = NormalizeGuid(a.DeviceGuid);

        return list;
    }

    /// <summary>
    /// ✅ Trả HashSet GUID đã normalize (UPPER+TRIM), dùng để filter AlertEvents/Realtime.
    /// </summary>
    public async Task<HashSet<string>> GetDeviceGuidsOfUserAsync(string userId, CancellationToken ct = default)
    {
        var list = await _col.Find(x => x.UserId == userId)
            .Project(x => x.DeviceGuid)
            .ToListAsync(ct);

        return new HashSet<string>(
            (list ?? new List<string>()).Select(NormalizeGuid).Where(x => !string.IsNullOrWhiteSpace(x)),
            StringComparer.OrdinalIgnoreCase
        );
    }

    // ✅ Alias rõ nghĩa (tuỳ bạn thích gọi)
    public Task<HashSet<string>> GetNormalizedDeviceGuidsOfUserAsync(string userId, CancellationToken ct = default)
        => GetDeviceGuidsOfUserAsync(userId, ct);

    public Task<bool> IsAssignedAsync(string userId, string deviceGuid, CancellationToken ct = default)
    {
        deviceGuid = NormalizeGuid(deviceGuid);
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(deviceGuid))
            return Task.FromResult(false);

        return _col.Find(x => x.UserId == userId && x.DeviceGuid == deviceGuid).AnyAsync(ct);
    }

    public Task AssignAsync(string userId, string deviceGuid, string? deviceName, CancellationToken ct = default)
    {
        deviceGuid = NormalizeGuid(deviceGuid);
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("userId required");
        if (string.IsNullOrWhiteSpace(deviceGuid)) throw new ArgumentException("deviceGuid required");

        var filter = Builders<ElitechDeviceAssignment>.Filter.Where(x => x.UserId == userId && x.DeviceGuid == deviceGuid);
        var update = Builders<ElitechDeviceAssignment>.Update
            .SetOnInsert(x => x.UserId, userId)
            .SetOnInsert(x => x.DeviceGuid, deviceGuid)
            .Set(x => x.DeviceName, deviceName)
            .Set(x => x.AssignedAtUtc, DateTime.UtcNow);

        return _col.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
    }

    public async Task<bool> UnassignAsync(string userId, string deviceGuid, CancellationToken ct = default)
    {
        deviceGuid = NormalizeGuid(deviceGuid);
        var res = await _col.DeleteOneAsync(x => x.UserId == userId && x.DeviceGuid == deviceGuid, ct);
        return res.DeletedCount > 0;
    }

    // ✅ nếu cần: xoá hết device của 1 user (reset)
    public async Task<long> UnassignAllByUserAsync(string userId, CancellationToken ct = default)
    {
        var res = await _col.DeleteManyAsync(x => x.UserId == userId, ct);
        return res.DeletedCount;
    }

    public async Task<List<ElitechDeviceAssignment>> GetAllDevicesAsync(CancellationToken ct = default)
    {
        // Lấy tất cả assignment, group theo DeviceGuid để ra danh sách thiết bị unique
        var list = await _col.Aggregate()
            .SortByDescending(x => x.AssignedAtUtc)
            .Group(x => x.DeviceGuid, g => g.First())
            .ToListAsync(ct);

        foreach (var a in list)
            a.DeviceGuid = NormalizeGuid(a.DeviceGuid);

        return list;
    }

    // ✅ Alias để Controller khỏi lỗi CS1061
    public Task<List<ElitechDeviceAssignment>> GetAssignedDevicesByUserAsync(string userId, CancellationToken ct = default)
        => GetByUserAsync(userId, ct);

    // ✅ Gọn cho UI: trả list device unique theo user (guid + name) - normalize guid
    public async Task<List<(string DeviceGuid, string? DeviceName)>> GetAssignedDeviceBriefByUserAsync(
        string userId,
        CancellationToken ct = default)
    {
        var list = await _col.Find(x => x.UserId == userId)
            .SortBy(x => x.DeviceGuid)
            .Project(x => new { x.DeviceGuid, x.DeviceName })
            .ToListAsync(ct);

        return (list ?? new())
            .Select(x => (NormalizeGuid(x.DeviceGuid), x.DeviceName))
            .Where(x => !string.IsNullOrWhiteSpace(x.Item1))
            .DistinctBy(x => x.Item1)
            .ToList();
    }

    // (OPTIONAL) ✅ Helper filter để query AlertEvents kiểu: x.UserId==... && !x.IsRead && assigned.Contains(x.DeviceGuid)
    // Lưu ý: MongoDB LINQ Contains(HashSet) thường OK, nhưng nếu bạn muốn FilterDefinition chuẩn hơn thì dùng cái này.
    public async Task<FilterDefinition<TDoc>> GetAssignedGuidsFilterOfUserAsync<TDoc>(
        string userId,
        System.Linq.Expressions.Expression<Func<TDoc, string>> guidField,
        CancellationToken ct = default)
    {
        var set = await GetDeviceGuidsOfUserAsync(userId, ct);
        if (set.Count == 0)
            return Builders<TDoc>.Filter.Where(_ => false); // empty -> no match

        // $in
        return Builders<TDoc>.Filter.In(guidField, set);
    }
}