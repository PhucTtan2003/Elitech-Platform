using Elitech.Hubs;
using Elitech.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;

namespace Elitech.Infrastructure;

public class ElitechAlarmRealtimeWorker : BackgroundService
{
    private readonly ILogger<ElitechAlarmRealtimeWorker> _logger;
    private readonly ElitechApiClient _api;
    private readonly IHubContext<ElitechAlarmHub> _hub;


    // cache lastSeen per deviceGuid
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _cfg;

    // interval poll server-side
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WindowBack = TimeSpan.FromMinutes(5);

    public ElitechAlarmRealtimeWorker(
        ILogger<ElitechAlarmRealtimeWorker> logger,
        ElitechApiClient api,
        IHubContext<ElitechAlarmHub> hub,
        IMemoryCache cache,
        IConfiguration cfg)
    {
        _logger = logger;
        _api = api;
        _hub = hub;
        _cache = cache;
        _cfg = cfg;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ElitechAlarmRealtimeWorker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deviceGuids = await GetActiveDeviceGuidsAsync(stoppingToken);

                foreach (var deviceGuid in deviceGuids)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    await PullAndBroadcastForDevice(deviceGuid, stoppingToken);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AlarmRealtimeWorker loop error");
            }

            // ⬇️ interval từ appsettings
            var sec = _cfg.GetValue<int>("AlarmRealtime:PollSeconds", 300);
            sec = Math.Clamp(sec, 30, 3600);

            await Task.Delay(TimeSpan.FromSeconds(sec), stoppingToken);
        }
    }


    private async Task<List<string>> GetActiveDeviceGuidsAsync(CancellationToken ct)
    {
        // ✅ Cách 1 (demo): dùng API getDeviceGuids
        // (Bạn có thể thay bằng device được assign trong Mongo theo user để giảm tải)
        var resp = await _api.GetDeviceGuidsAsync(ct);
        return resp?.Data?.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
               ?? new List<string>();
    }

    private static string NormalizeGuid(string? s)
    => string.IsNullOrWhiteSpace(s) ? "" : s.Trim().ToUpperInvariant();

    private static long NormUnixSeconds(long ts)
    {
        if (ts <= 0) return 0;
        return ts > 10_000_000_000L ? (ts / 1000) : ts; // ms -> sec
    }

    private async Task PullAndBroadcastForDevice(string deviceGuid, CancellationToken ct)
    {
        var guid = NormalizeGuid(deviceGuid);
        if (string.IsNullOrWhiteSpace(guid)) return;

        // ✅ dùng guid đã normalize cho cache key để không bị lệch
        var keyLast = $"alarm:last:{guid}";
        var lastSeen = _cache.TryGetValue<long>(keyLast, out var v) ? v : 0L;
        lastSeen = NormUnixSeconds(lastSeen);

        var to = DateTimeOffset.UtcNow;
        DateTimeOffset from;

        if (lastSeen > 0)
        {
            // ✅ lấy lại 1 chút để không miss record do delay
            var safeFrom = DateTimeOffset.FromUnixTimeSeconds(lastSeen).Subtract(WindowBack);

            // clamp: không cho quá sâu (tuỳ bạn chỉnh 24h/7d)
            var minFrom = to.AddHours(-24);
            from = safeFrom < minFrom ? minFrom : safeFrom;
        }
        else
        {
            from = to.AddHours(-24);
        }

        // subUid thường = 0
        var resp = await _api.GetAlarmRecordAsync(guid, subUid: 0, from, to, ct);

        if (resp == null || resp.code != 0 || resp.data == null || resp.data.Count == 0)
            return;

        // ✅ sort theo timestamp đã normalize
        var items = resp.data
            .Where(x => x != null)
            .Select(x =>
            {
                x.alarmTimeStamp = x.alarmTimeStamp.HasValue ? NormUnixSeconds(x.alarmTimeStamp.Value) : (long?)null;
                return x;
            })
            .OrderBy(x => x.alarmTimeStamp ?? 0)
            .ToList();

        // ✅ chỉ lấy record mới hơn lastSeen
        var newOnes = lastSeen > 0
            ? items.Where(x => (x.alarmTimeStamp ?? 0) > lastSeen).ToList()
            : items;

        if (newOnes.Count == 0) return;

        var maxTs = newOnes.Max(x => x.alarmTimeStamp ?? 0);
        if (maxTs > 0)
            _cache.Set(keyLast, maxTs, TimeSpan.FromHours(6));

        // ✅ group cũng phải dùng guid normalize cho trùng client join group
        var group = $"dev:{guid}";
        await _hub.Clients.Group(group).SendAsync("alarm.new", newOnes, ct);
    }

}
