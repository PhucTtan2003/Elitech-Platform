using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Elitech.Models;
using Elitech.Hubs;
using Elitech.Models.Alerts;
using Elitech.Models.AlertRule;
using Elitech.Models.RealtimeData;
using Elitech.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.SignalR;

namespace Elitech.Workers;

public class ElitechAlertWorker : BackgroundService
{
    private readonly ILogger<ElitechAlertWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    // =========================================================
    // WORKER INTERVAL
    // =========================================================
    // Giữ nguyên chu kỳ hiện tại của bạn.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(160);

    // Log miss tối đa 1 lần / 2 phút / user-device
    private static readonly Dictionary<string, DateTime> _missLogGate =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object _gateLock = new();

    public ElitechAlertWorker(
        ILogger<ElitechAlertWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ElitechAlertWorker started interval={IntervalSec}s",
            Interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "ElitechAlertWorker tick error");
            }

            if (!await SafeDelayAsync(Interval, stoppingToken))
                break;
        }

        _logger.LogInformation(
            "ElitechAlertWorker stopped");
    }

    // =========================================================
    // MAIN TICK
    // =========================================================
    private async Task ProcessTickAsync(
        CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var ruleSvc =
            scope.ServiceProvider
                .GetRequiredService<ElitechAlertRuleService>();

        var eventSvc =
            scope.ServiceProvider
                .GetRequiredService<ElitechAlertEventService>();

        var api =
            scope.ServiceProvider
                .GetRequiredService<ElitechApiClient>();

        var assignSvc =
            scope.ServiceProvider
                .GetRequiredService<ElitechDeviceAssignmentService>();

        var accounts =
            scope.ServiceProvider
                .GetRequiredService<AccountService>();

        var smsSvc =
            scope.ServiceProvider
                .GetRequiredService<AlertSmsService>();

        var hub =
            scope.ServiceProvider
                .GetRequiredService<IHubContext<ElitechAlarmHub>>();

        var notifyOpt =
            scope.ServiceProvider
                .GetRequiredService<IOptions<AlertNotifyOptions>>()
                .Value;

        var rtCache =
            scope.ServiceProvider
                .GetService<ElitechRealtimeCacheService>();

        // =====================================================
        // SMS COOLDOWN
        // =====================================================
        var smsCooldownMinutes =
            Math.Max(1, notifyOpt.SmsCooldownMinutes);

        var smsCooldown =
            TimeSpan.FromMinutes(smsCooldownMinutes);

        // =====================================================
        // LOAD ENABLED RULES
        // =====================================================
        var rules =
            await ruleSvc.GetAllEnabledAsync(stoppingToken);

        if (rules == null || rules.Count == 0)
        {
            _logger.LogDebug(
                "ALERT: no enabled rules");

            return;
        }

        _logger.LogInformation(
            "ALERT: tick started enabledRules={RuleCount}",
            rules.Count);

        // =====================================================
        // LOAD DEVICE GUIDS VISIBLE TO ELITECH API
        // =====================================================
        HashSet<string> visibleSet;

        try
        {
            var visResp =
                await api.GetDeviceGuidsAsync(stoppingToken);

            var visCode =
                visResp?.Code ?? -1;

            if (visCode != 0)
            {
                _logger.LogWarning(
                    "ALERT: getDeviceGuids code!=0 " +
                    "code={Code} message={Message}",
                    visCode,
                    Short(visResp?.Message));

                return;
            }

            visibleSet =
                (visResp?.Data ?? new List<string>())
                    .Select(NormalizeGuid)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToHashSet(
                        StringComparer.OrdinalIgnoreCase);

            _logger.LogInformation(
                "ALERT: visible devices count={Count}",
                visibleSet.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "ALERT: getDeviceGuids exception -> skip tick");

            return;
        }

        // Cache account exists trong 1 tick
        var userExistsCache =
            new Dictionary<string, bool>(
                StringComparer.OrdinalIgnoreCase);

        // =====================================================
        // PROCESS EACH USER
        // =====================================================
        foreach (var gUser in rules.GroupBy(
                     r => (r.UserId ?? "").Trim()))
        {
            stoppingToken.ThrowIfCancellationRequested();

            var userId = gUser.Key;

            if (string.IsNullOrWhiteSpace(userId))
            {
                _logger.LogWarning(
                    "ALERT: rule group has empty userId");

                continue;
            }

            var userRules =
                gUser.ToList();

            // =================================================
            // ACCOUNT MUST EXIST
            // =================================================
            if (!await UserExistsAsync(
                    accounts,
                    userId,
                    userExistsCache,
                    stoppingToken))
            {
                _logger.LogWarning(
                    "ALERT: orphan rules -> skip " +
                    "userId={UserId} rules={Count}",
                    userId,
                    userRules.Count);

                continue;
            }

            // =================================================
            // ASSIGNED DEVICES
            // =================================================
            HashSet<string> assignedSet;

            try
            {
                assignedSet =
                    await assignSvc
                        .GetDeviceGuidsOfUserAsync(
                            userId,
                            stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "ALERT: cannot load assigned devices " +
                    "userId={UserId}",
                    userId);

                continue;
            }

            assignedSet ??=
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            // Normalize assignment set để tránh lệch case
            assignedSet =
                assignedSet
                    .Select(NormalizeGuid)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToHashSet(
                        StringComparer.OrdinalIgnoreCase);

            // =================================================
            // GUIDS FROM RULES
            // =================================================
            var allRuleGuids =
                userRules
                    .Select(r => NormalizeGuid(r.DeviceGuid))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            var ruleGuids =
                allRuleGuids
                    .Where(g => assignedSet.Contains(g))
                    .ToArray();

            var unassignedRuleGuids =
                allRuleGuids
                    .Where(g => !assignedSet.Contains(g))
                    .ToArray();

            if (unassignedRuleGuids.Length > 0)
            {
                _logger.LogWarning(
                    "ALERT: skip rules for unassigned devices " +
                    "userId={UserId} guids={Guids}",
                    userId,
                    string.Join(
                        ",",
                        unassignedRuleGuids));
            }

            if (ruleGuids.Length == 0)
            {
                _logger.LogWarning(
                    "ALERT: user {UserId} has rules " +
                    "but no assigned rule devices.",
                    userId);

                continue;
            }

            // =================================================
            // FILTER DEVICES VISIBLE TO ELITECH API
            // =================================================
            var guidsVisible =
                ruleGuids
                    .Where(g => visibleSet.Contains(g))
                    .ToArray();

            var guidsMissing =
                ruleGuids
                    .Where(g => !visibleSet.Contains(g))
                    .ToArray();

            if (guidsMissing.Length > 0)
            {
                _logger.LogWarning(
                    "ALERT: ruleGuids NOT VISIBLE to API " +
                    "userId={UserId} missing={Missing} all={All}",
                    userId,
                    string.Join(",", guidsMissing),
                    string.Join(",", ruleGuids));

                foreach (var missingGuid in guidsMissing)
                {
                    await EnsureStateExistsAsync(
                        eventSvc,
                        userId,
                        missingGuid,
                        stoppingToken);
                }
            }

            if (guidsVisible.Length == 0)
            {
                _logger.LogWarning(
                    "ALERT: userId={UserId} has rules " +
                    "but 0 visible guids -> skip.",
                    userId);

                continue;
            }

            var reqGuidsStr =
                string.Join(",", guidsVisible);

            // =================================================
            // REALTIME
            //
            // QUAN TRỌNG:
            // Worker KHÔNG throttle/cache riêng nữa.
            // Toàn bộ realtime phải đi qua ElitechApiClient.
            // =================================================
            RealTimeResp rt;

            try
            {
                rt =
                    await api.GetRealTimeDataAsync(
                        guidsVisible,
                        stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "ALERT: realtime pull EXCEPTION " +
                    "userId={UserId} reqGuids={Req}",
                    userId,
                    reqGuidsStr);

                continue;
            }

            if (rt == null)
            {
                _logger.LogWarning(
                    "ALERT: realtime resp NULL " +
                    "userId={UserId} reqGuids={Req}",
                    userId,
                    reqGuidsStr);

                continue;
            }

            if (rt.code != 0)
            {
                _logger.LogWarning(
                    "ALERT: realtime code!=0 " +
                    "userId={UserId} code={Code} " +
                    "message={Message} msg={Msg} " +
                    "error={Err} time={Time} " +
                    "reqGuids={Req}",
                    userId,
                    rt.code,
                    Short(rt.message),
                    Short(rt.msg),
                    Short(rt.error),
                    Short(rt.time),
                    reqGuidsStr);

                continue;
            }

            var rows =
                rt.data ?? new List<RealTimeItem>();

            if (rows.Count == 0)
            {
                _logger.LogWarning(
                    "ALERT: realtime rows=0 but code=0 " +
                    "userId={UserId} reqGuids={Req}",
                    userId,
                    reqGuidsStr);

                foreach (var guid in guidsVisible)
                {
                    await EnsureStateExistsAsync(
                        eventSvc,
                        userId,
                        guid,
                        stoppingToken);
                }

                continue;
            }

            var returnedGuids =
                rows
                    .Select(x =>
                        NormalizeGuid(x?.deviceGuid))
                    .Where(x =>
                        !string.IsNullOrWhiteSpace(x))
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .Take(20)
                    .ToArray();

            _logger.LogInformation(
                "ALERT: realtime OK " +
                "userId={UserId} rows={Rows} " +
                "reqGuids={Req} returnedGuids={Returned}",
                userId,
                rows.Count,
                reqGuidsStr,
                string.Join(",", returnedGuids));

            // =================================================
            // OPTIONAL SHARED REALTIME CACHE
            // =================================================
            if (rtCache != null)
            {
                foreach (var realtimeRow in rows)
                {
                    var g =
                        NormalizeGuid(
                            realtimeRow.deviceGuid);

                    if (!string.IsNullOrWhiteSpace(g))
                    {
                        rtCache.Upsert(
                            g,
                            realtimeRow);
                    }
                }
            }

            // =================================================
            // MAP REALTIME BY DEVICE GUID
            // =================================================
            var map =
                new Dictionary<string, RealTimeItem>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var realtimeRow in rows)
            {
                var g =
                    NormalizeGuid(
                        realtimeRow.deviceGuid);

                if (!string.IsNullOrWhiteSpace(g))
                {
                    map[g] = realtimeRow;
                }
            }

            // =================================================
            // EVALUATE EACH RULE
            // =================================================
            foreach (var rule in userRules)
            {
                stoppingToken.ThrowIfCancellationRequested();

                var guid =
                    NormalizeGuid(
                        rule.DeviceGuid);

                // =============================================
                // FIX QUAN TRỌNG:
                // Trước đây block này bị viết sai và continue
                // vô điều kiện làm mọi rule không Evaluate.
                // =============================================
                if (string.IsNullOrWhiteSpace(guid))
                {
                    _logger.LogWarning(
                        "ALERT: rule has empty guid " +
                        "userId={UserId} ruleId={RuleId}",
                        userId,
                        rule.Id);

                    continue;
                }

                if (!assignedSet.Contains(guid))
                {
                    _logger.LogWarning(
                        "ALERT: rule skipped - " +
                        "device not assigned " +
                        "userId={UserId} guid={Guid}",
                        userId,
                        guid);

                    continue;
                }

                if (!visibleSet.Contains(guid))
                {
                    _logger.LogWarning(
                        "ALERT: rule skipped - " +
                        "device not visible " +
                        "userId={UserId} guid={Guid}",
                        userId,
                        guid);

                    continue;
                }

                // =============================================
                // REALTIME ROW MUST EXIST
                // =============================================
                if (!map.TryGetValue(
                        guid,
                        out var row))
                {
                    if (ShouldLogMiss(
                            userId,
                            guid))
                    {
                        _logger.LogWarning(
                            "ALERT: realtime MISSING " +
                            "guid={Guid} userId={UserId} " +
                            "rtRows={Rows} mapKeysCount={MapCount} " +
                            "reqGuids={Req} returnedGuids={Returned}",
                            guid,
                            userId,
                            rows.Count,
                            map.Count,
                            reqGuidsStr,
                            string.Join(
                                ",",
                                returnedGuids));
                    }

                    await EnsureStateExistsAsync(
                        eventSvc,
                        userId,
                        guid,
                        stoppingToken);

                    continue;
                }

                // =============================================
                // SAMPLE TIMESTAMP
                // =============================================
                var sampleTs =
                    TryGetSampleTs(row);

                if (!sampleTs.HasValue)
                {
                    _logger.LogWarning(
                        "ALERT: sampleTs NULL " +
                        "userId={UserId} guid={Guid} " +
                        "lastSessionTime={LastSession} " +
                        "uploadTime={Upload} " +
                        "lastAddressTime={LastAddr}",
                        userId,
                        guid,
                        row.lastSessionTime,
                        row.uploadTime,
                        row.lastAddressTime);
                }

                // =============================================
                // LOAD STATE
                // =============================================
                var state =
                    await eventSvc.GetStateAsync(
                        userId,
                        guid,
                        stoppingToken);

                var created = false;

                if (state == null)
                {
                    state =
                        new ElitechAlertState
                        {
                            UserId = userId,
                            DeviceGuid = guid,

                            IsBad = false,
                            ConsecutiveBadHits = 0,

                            LastReasons = "",

                            LastAlertAtUtc = null,
                            LastSmsAtUtc = null,

                            LastSampleTs = null
                        };

                    created = true;

                    await eventSvc.UpsertStateAsync(
                        state,
                        stoppingToken);

                    _logger.LogInformation(
                        "ALERT: state CREATED " +
                        "userId={UserId} guid={Guid}",
                        userId,
                        guid);
                }

                // =============================================
                // DO NOT COUNT SAME/OLD SAMPLE AGAIN
                //
                // Ví dụ:
                // 10:00 sample = 30.5
                // Worker chạy 3 lần nhưng API vẫn trả sample 10:00
                //
                // Không được biến thành 3 debounce hits.
                // =============================================
                if (sampleTs.HasValue &&
                    state.LastSampleTs.HasValue &&
                    sampleTs.Value <= state.LastSampleTs.Value)
                {
                    _logger.LogDebug(
                        "ALERT: skip duplicate/old sample " +
                        "userId={UserId} guid={Guid} " +
                        "sampleTs={SampleTs} lastSampleTs={LastSampleTs}",
                        userId,
                        guid,
                        sampleTs.Value,
                        state.LastSampleTs.Value);

                    continue;
                }

                // =============================================
                // PARSE TEMPERATURE / HUMIDITY
                // =============================================
                var temps =
                    new double?[4]
                    {
                        ElitechAlertEngine.ParseNumber(row.tmp1),
                        ElitechAlertEngine.ParseNumber(row.tmp2),
                        ElitechAlertEngine.ParseNumber(row.tmp3),
                        ElitechAlertEngine.ParseNumber(row.tmp4)
                    };

                var hums =
                    new double?[2]
                    {
                        ElitechAlertEngine.ParseNumber(row.hum1),
                        ElitechAlertEngine.ParseNumber(row.hum2)
                    };

                // =============================================
                // EVALUATE RULE
                // =============================================
                var (isBadRaw, reasons) =
                    ElitechAlertEngine.Evaluate(
                        rule,
                        temps,
                        hums);

                var reasonsNorm =
                    (reasons ?? "").Trim();

                // =============================================
                // ALERT PER NEW SAMPLE
                //
                // YÊU CẦU:
                // - Không dùng DebounceHits để trì hoãn cảnh báo.
                // - Mỗi SAMPLE MỚI vượt ngưỡng => tạo 1 alert ngay.
                // - Cùng một sample cũ đã được chặn phía trên bằng LastSampleTs.
                // - SMS vẫn có cooldown riêng, KHÔNG phụ thuộc fire của toast/event.
                // =============================================
                if (isBadRaw)
                {
                    state.ConsecutiveBadHits =
                        Math.Min(
                            state.ConsecutiveBadHits + 1,
                            999);
                }
                else
                {
                    state.ConsecutiveBadHits = 0;
                }

                var nowBad = isBadRaw;
                var nowUtc = DateTime.UtcNow;

                // Mỗi sample mới vượt ngưỡng => fire ngay.
                // Không còn 1/2, 2/2, 3/2, %2 hay event cooldown.
                var fire = isBadRaw;

                _logger.LogInformation(
                    "ALERT: EVAL " +
                    "userId={UserId} guid={Guid} " +
                    "tmp1={Tmp1} tmp2={Tmp2} " +
                    "hum1={Hum1} hum2={Hum2} " +
                    "badRaw={BadRaw} reasons={Reasons} " +
                    "sampleTs={SampleTs} FIRE={Fire}",
                    userId,
                    guid,
                    temps[0],
                    temps[1],
                    hums[0],
                    hums[1],
                    isBadRaw,
                    reasonsNorm,
                    sampleTs,
                    fire);

                // =============================================
                // CREATE ALERT EVENT
                // =============================================
                if (fire)
                {
                    var deviceName =
                        !string.IsNullOrWhiteSpace(row.deviceName)
                            ? row.deviceName
                            : rule.DeviceName;

                    if (string.IsNullOrWhiteSpace(deviceName))
                        deviceName = guid;

                    DateTime occurredUtc;

                    try
                    {
                        occurredUtc =
                            sampleTs.HasValue
                                ? DateTimeOffset
                                    .FromUnixTimeSeconds(
                                        sampleTs.Value)
                                    .UtcDateTime
                                : nowUtc;
                    }
                    catch
                    {
                        occurredUtc = nowUtc;
                    }

                    var alertEvent =
                        new ElitechAlertEvent
                        {
                            UserId = userId,

                            DeviceGuid = guid,
                            DeviceName = deviceName,

                            OccurredAtUtc = occurredUtc,

                            Tmp1 = temps[0],
                            Tmp2 = temps[1],
                            Tmp3 = temps[2],
                            Tmp4 = temps[3],

                            Hum1 = hums[0],
                            Hum2 = hums[1],

                            Reasons = reasonsNorm,

                            Level = "ALARM",
                            IsRead = false
                        };

                    await eventSvc.InsertEventAsync(
                        alertEvent,
                        stoppingToken);

                    _logger.LogWarning(
                        "ALERT: EVENT INSERTED " +
                        "userId={UserId} guid={Guid} " +
                        "deviceName={DeviceName} " +
                        "occurredUtc={Occurred} " +
                        "reasons={Reasons} " +
                        "tmp1={Tmp1} hum1={Hum1}",
                        userId,
                        guid,
                        deviceName,
                        occurredUtc,
                        reasonsNorm,
                        temps[0],
                        hums[0]);

                    // =========================================
                    // REALTIME PUSH -> TOAST / ALARM AUDIO
                    // Mỗi sample mới vượt ngưỡng đều push ngay.
                    // SMS cooldown bên dưới vẫn giữ riêng.
                    // =========================================
                    try
                    {
                        var pushPayload = new
                        {
                            deviceGuid = guid,
                            deviceName = deviceName,
                            level = "high",
                            title = "CẢNH BÁO VƯỢT NGƯỠNG",
                            message =
                                $"{deviceName}: {reasonsNorm} | " +
                                $"Temp1={FormatValue(temps[0])}°C | " +
                                $"Hum1={FormatValue(hums[0])}%RH",
                            reasons = reasonsNorm,
                            tmp1 = temps[0],
                            tmp2 = temps[1],
                            tmp3 = temps[2],
                            tmp4 = temps[3],
                            hum1 = hums[0],
                            hum2 = hums[1],
                            occurredAtUtc = occurredUtc,
                            ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        };

                        await hub.Clients
                            .Group($"user:{userId}")
                            .SendAsync(
                                "alert.toast",
                                pushPayload,
                                stoppingToken);

                        _logger.LogWarning(
                            "ALERT: SIGNALR PUSH " +
                            "userId={UserId} guid={Guid} " +
                            "reasons={Reasons}",
                            userId,
                            guid,
                            reasonsNorm);
                    }
                    catch (OperationCanceledException)
                        when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // SignalR lỗi không được làm mất SMS / state update.
                        _logger.LogWarning(
                            ex,
                            "ALERT: SIGNALR PUSH FAIL " +
                            "userId={UserId} guid={Guid}",
                            userId,
                            guid);
                    }

                    state.LastAlertAtUtc =
                        nowUtc;

                    state.LastReasons =
                        reasonsNorm;

                    // =========================================
                    // SMS COOLDOWN
                    // =========================================
                    var canSms =
                        state.LastSmsAtUtc == null ||
                        (
                            nowUtc -
                            state.LastSmsAtUtc.Value
                        ) >= smsCooldown;

                    if (canSms)
                    {
                        try
                        {
                            var smsText =
$@"[ALERT] {deviceName} ({guid})
Level: ALARM
{reasonsNorm}
Temp1: {FormatValue(temps[0])} C
Hum1: {FormatValue(hums[0])} %RH
Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

                            var requestId =
                                $"ELI-{guid}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

                            var (ok, reasonFail) =
                                await smsSvc.SendToUserAsync(
                                    userId,
                                    smsText,
                                    requestId,
                                    stoppingToken);

                            if (ok)
                            {
                                state.LastSmsAtUtc =
                                    nowUtc;

                                _logger.LogWarning(
                                    "ALERT: SMS SENT " +
                                    "userId={UserId} " +
                                    "guid={Guid} " +
                                    "requestId={RequestId}",
                                    userId,
                                    guid,
                                    requestId);
                            }
                            else
                            {
                                _logger.LogWarning(
                                    "ALERT: SMS FAIL " +
                                    "userId={UserId} " +
                                    "guid={Guid} " +
                                    "reason={Reason} " +
                                    "requestId={RequestId}",
                                    userId,
                                    guid,
                                    Short(reasonFail),
                                    requestId);
                            }
                        }
                        catch (OperationCanceledException)
                            when (stoppingToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(
                                ex,
                                "ALERT: SMS EXCEPTION " +
                                "userId={UserId} guid={Guid}",
                                userId,
                                guid);
                        }
                    }
                    else
                    {
                        _logger.LogDebug(
                            "ALERT: SMS cooldown active " +
                            "userId={UserId} guid={Guid} " +
                            "lastSms={LastSms}",
                            userId,
                            guid,
                            state.LastSmsAtUtc);
                    }
                }

                // =============================================
                // UPDATE STATE
                // =============================================
                state.IsBad = nowBad;

                if (!nowBad)
                {
                    state.LastReasons = "";
                }

                // Chỉ lưu sampleTs sau khi sample đã được xử lý.
                if (sampleTs.HasValue)
                {
                    state.LastSampleTs =
                        sampleTs.Value;
                }

                await eventSvc.UpsertStateAsync(
                    state,
                    stoppingToken);

                if (!created)
                {
                    _logger.LogDebug(
                        "ALERT: state UPSERT " +
                        "userId={UserId} guid={Guid} " +
                        "isBad={IsBad} hits={Hits} " +
                        "lastSampleTs={LastSampleTs}",
                        userId,
                        guid,
                        state.IsBad,
                        state.ConsecutiveBadHits,
                        state.LastSampleTs);
                }
            }
        }

        _logger.LogInformation(
            "ALERT: tick completed");
    }

    // =========================================================
    // HELPERS
    // =========================================================

    private static string NormalizeGuid(
        string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? ""
            : value.Trim().ToUpperInvariant();
    }

    private static string Short(
        string? value,
        int length = 220)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        value =
            value
                .Replace("\r", " ")
                .Replace("\n", " ");

        return value.Length <= length
            ? value
            : value.Substring(0, length) + "...";
    }

    private static string FormatValue(
        double? value)
    {
        return value.HasValue
            ? value.Value.ToString("0.##")
            : "-";
    }

    // =========================================================
    // GET REALTIME SAMPLE TIMESTAMP
    // =========================================================
    private static long? TryGetSampleTs(
        RealTimeItem row)
    {
        try
        {
            long? ts =
                row.uploadTime ??
                row.lastSessionTime ??
                row.lastAddressTime;

            if (!ts.HasValue ||
                ts.Value <= 0)
            {
                return null;
            }

            // milliseconds -> seconds
            if (ts.Value > 10_000_000_000L)
            {
                ts /= 1000;
            }

            return ts;
        }
        catch
        {
            return null;
        }
    }

    // =========================================================
    // ENSURE ALERT STATE EXISTS
    // =========================================================
    private static async Task EnsureStateExistsAsync(
        ElitechAlertEventService eventSvc,
        string userId,
        string guid,
        CancellationToken ct)
    {
        guid =
            NormalizeGuid(guid);

        if (string.IsNullOrWhiteSpace(userId) ||
            string.IsNullOrWhiteSpace(guid))
        {
            return;
        }

        var state =
            await eventSvc.GetStateAsync(
                userId,
                guid,
                ct);

        if (state != null)
            return;

        state =
            new ElitechAlertState
            {
                UserId = userId,
                DeviceGuid = guid,

                IsBad = false,
                ConsecutiveBadHits = 0,

                LastReasons = "",

                LastAlertAtUtc = null,
                LastSmsAtUtc = null,

                LastSampleTs = null
            };

        await eventSvc.UpsertStateAsync(
            state,
            ct);
    }

    // =========================================================
    // THROTTLE MISSING LOGS
    // =========================================================
    private static bool ShouldLogMiss(
        string userId,
        string guid)
    {
        var key =
            $"{userId}|{guid}";

        var now =
            DateTime.UtcNow;

        lock (_gateLock)
        {
            if (_missLogGate.TryGetValue(
                    key,
                    out var last) &&
                now - last < TimeSpan.FromMinutes(2))
            {
                return false;
            }

            _missLogGate[key] =
                now;

            // cleanup nhẹ
            if (_missLogGate.Count > 1000)
            {
                var cutoff =
                    now - TimeSpan.FromMinutes(30);

                var oldKeys =
                    _missLogGate
                        .Where(x => x.Value < cutoff)
                        .Select(x => x.Key)
                        .ToList();

                foreach (var oldKey in oldKeys)
                {
                    _missLogGate.Remove(oldKey);
                }
            }

            return true;
        }
    }

    // =========================================================
    // ACCOUNT EXISTS
    // =========================================================
    private static async Task<bool> UserExistsAsync(
        AccountService accounts,
        string userId,
        Dictionary<string, bool> cache,
        CancellationToken ct)
    {
        if (cache.TryGetValue(
                userId,
                out var exists))
        {
            return exists;
        }

        var account =
            await accounts.GetByIdAsync(
                userId,
                ct);

        exists =
            account != null;

        cache[userId] =
            exists;

        return exists;
    }

    // =========================================================
    // SAFE DELAY
    // =========================================================
    private static async Task<bool> SafeDelayAsync(
        TimeSpan delay,
        CancellationToken ct)
    {
        try
        {
            await Task.Delay(
                delay,
                ct);

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}