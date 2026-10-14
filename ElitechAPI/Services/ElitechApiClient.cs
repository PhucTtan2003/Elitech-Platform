using Elitech.Models;
using Elitech.Models.DeviceInfo;
using Elitech.Models.DeviceParams;
using Elitech.Models.HistoryData;
using Elitech.Models.RealtimeData;
using Elitech.Models.SetParam;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static Elitech.Models.Waybill.BatchWaybillViewModel;

namespace Elitech.Services;

public class ElitechApiClient
{
    private readonly HttpClient _http;
    private readonly ElitechOptions _opt;
    private readonly HttpClient _httpAlarm;
    private readonly ElitechAlarmOptions _alarmOpt;
    private readonly ElitechV2LegacyProjectionService _legacyProjection;
    // token cache - Elitech legacy/main host
    private string? _cachedToken;
    private DateTimeOffset _tokenGotAt;

    // token anti-spam (5110) - Elitech legacy/main host
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private DateTimeOffset _lastTokenAttemptAt = DateTimeOffset.MinValue;

    // token cache - Elitech V2 host (i-elitech.com)
    private string? _cachedV2Token;
    private DateTimeOffset _v2TokenGotAt;
    private readonly SemaphoreSlim _v2TokenLock = new(1, 1);
    private DateTimeOffset _lastV2TokenAttemptAt = DateTimeOffset.MinValue;

    // device guids cache
    private List<string>? _cachedDeviceGuids;
    private DateTimeOffset _deviceGuidsCachedAt;
    private static string NormalizeGuid(string s) => s.Trim().ToUpperInvariant();

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };
    // ===== REALTIME CACHE / RATE LIMIT THEO GUID =====
    private static readonly SemaphoreSlim _rtGate = new(1, 1);

    private static readonly Dictionary<string, (DateTime HitUtc, RealTimeResp Resp)>
        _rtCacheByKey = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan _rtMinInterval =
        TimeSpan.FromSeconds(60);

    public ElitechApiClient(
         IHttpClientFactory factory,
         ElitechOptions opt,
         ElitechAlarmOptions alarmOpt,
         ElitechV2LegacyProjectionService legacyProjection)
    {
        _opt = opt;
        _alarmOpt = alarmOpt;
        _legacyProjection = legacyProjection;

        _http = factory.CreateClient("ElitechMain");
        _httpAlarm = factory.CreateClient("ElitechAlarm");

        _http.BaseAddress = new Uri(_opt.BaseUrl.TrimEnd('/'));
        _httpAlarm.BaseAddress = new Uri(_alarmOpt.BaseUrl.TrimEnd('/'));
    }

    // =========================
    // Token (fix 5110: once per minute)
    // =========================
    private async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        // dùng token cache nếu còn hạn
        if (!string.IsNullOrEmpty(_cachedToken) &&
            DateTimeOffset.UtcNow - _tokenGotAt < TimeSpan.FromMinutes(30))
            return _cachedToken!;

        await _tokenLock.WaitAsync(ct);
        try
        {
            // double check sau lock
            if (!string.IsNullOrEmpty(_cachedToken) &&
                DateTimeOffset.UtcNow - _tokenGotAt < TimeSpan.FromMinutes(30))
                return _cachedToken!;

            // cooldown 60s để không spam getToken
            var now = DateTimeOffset.UtcNow;
            var since = now - _lastTokenAttemptAt;
            if (since < TimeSpan.FromSeconds(60))
            {
                var wait = TimeSpan.FromSeconds(60) - since;
                await Task.Delay(wait, ct);
            }

            _lastTokenAttemptAt = DateTimeOffset.UtcNow;

            var payload = await PostAsync<TokenResp>(
                "/api/data-api/elitechAccess/getToken",
                new
                {
                    keyId = _opt.KeyId,
                    keySecret = _opt.KeySecret,
                    userName = _opt.UserName,
                    password = _opt.Password
                },
                auth: false,
                ct: ct);

            if (payload == null)
                throw new InvalidOperationException("Empty token response");

            var token = payload.data?.Trim();

            if (string.IsNullOrWhiteSpace(token))
            {
                // nếu đúng 5110, chờ và retry 1 lần
                if (payload.code == 5110)
                {
                    await Task.Delay(TimeSpan.FromSeconds(61), ct);

                    _lastTokenAttemptAt = DateTimeOffset.UtcNow;

                    var payload2 = await PostAsync<TokenResp>(
                        "/api/data-api/elitechAccess/getToken",
                        new
                        {
                            keyId = _opt.KeyId,
                            keySecret = _opt.KeySecret,
                            userName = _opt.UserName,
                            password = _opt.Password
                        },
                        auth: false,
                        ct: ct);

                    token = payload2?.data?.Trim();
                    if (string.IsNullOrWhiteSpace(token))
                        throw new InvalidOperationException(
                            $"Cannot get token after retry. code={payload2?.code}, msg={payload2?.msg ?? payload2?.message ?? payload2?.error}");
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Cannot get token. code={payload.code}, msg={payload.msg ?? payload.message ?? payload.error}");
                }
            }

            // chuẩn hoá Bearer
            if (token!.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase))
                token = "Bearer " + token.Substring(7);
            else if (!token.StartsWith("Bearer ", StringComparison.Ordinal))
                token = "Bearer " + token;

            _cachedToken = token;
            _tokenGotAt = DateTimeOffset.UtcNow;
            return _cachedToken!;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // =========================
    // Token V2 - i-elitech.com
    // Tách riêng token/cache với host legacy để không dùng chéo token.
    // =========================
    private async Task<string> GetV2TokenAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_cachedV2Token) &&
            DateTimeOffset.UtcNow - _v2TokenGotAt < TimeSpan.FromMinutes(30))
        {
            return _cachedV2Token!;
        }

        await _v2TokenLock.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedV2Token) &&
                DateTimeOffset.UtcNow - _v2TokenGotAt < TimeSpan.FromMinutes(30))
            {
                return _cachedV2Token!;
            }

            var now = DateTimeOffset.UtcNow;
            var since = now - _lastV2TokenAttemptAt;

            if (since < TimeSpan.FromSeconds(60))
            {
                await Task.Delay(TimeSpan.FromSeconds(60) - since, ct);
            }

            _lastV2TokenAttemptAt = DateTimeOffset.UtcNow;

            var payload = await PostV2Async<TokenResp>(
                endpoint: "/api/data-api/elitechAccess/getToken",
                body: new
                {
                    keyId = _alarmOpt.KeyId,
                    keySecret = _alarmOpt.KeySecret,
                    userName = _alarmOpt.UserName,
                    password = _alarmOpt.Password
                },
                auth: false,
                ct: ct);

            if (payload == null)
                throw new InvalidOperationException("Empty V2 token response");

            var token = payload.data?.Trim();

            if (string.IsNullOrWhiteSpace(token) && payload.code == 5110)
            {
                await Task.Delay(TimeSpan.FromSeconds(61), ct);
                _lastV2TokenAttemptAt = DateTimeOffset.UtcNow;

                var retryPayload = await PostV2Async<TokenResp>(
                    endpoint: "/api/data-api/elitechAccess/getToken",
                    body: new
                    {
                        keyId = _alarmOpt.KeyId,
                        keySecret = _alarmOpt.KeySecret,
                        userName = _alarmOpt.UserName,
                        password = _alarmOpt.Password
                    },
                    auth: false,
                    ct: ct);

                token = retryPayload?.data?.Trim();

                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new InvalidOperationException(
                        $"Cannot get V2 token after retry. code={retryPayload?.code}, " +
                        $"msg={retryPayload?.msg ?? retryPayload?.message ?? retryPayload?.error}");
                }
            }
            else if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    $"Cannot get V2 token. code={payload.code}, " +
                    $"msg={payload.msg ?? payload.message ?? payload.error}");
            }

            _cachedV2Token = NormalizeBearerToken(token!);
            _v2TokenGotAt = DateTimeOffset.UtcNow;

            return _cachedV2Token;
        }
        finally
        {
            _v2TokenLock.Release();
        }
    }

    private static string NormalizeBearerToken(string token)
    {
        token = token.Trim();

        if (token.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase))
            return "Bearer " + token[7..].Trim();

        return "Bearer " + token;
    }

    // =========================
    // Core Post
    // =========================
    private async Task<T?> PostAsync<T>(string endpoint, object body, bool auth, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);

        if (auth)
        {
            var token = await GetTokenAsync(ct);
            req.Headers.Authorization = AuthenticationHeaderValue.Parse(token);
        }

        req.Content = new StringContent(
            JsonSerializer.Serialize(body, _jsonOpts),
            Encoding.UTF8,
            "application/json");

        using var res = await _http.SendAsync(req, ct);
        var content = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)res.StatusCode} when calling {endpoint}. Body: {content}");

        try
        {
            return JsonSerializer.Deserialize<T>(content, _jsonOpts);
        }
        catch (Exception ex)
        {
            throw new JsonException($"Deserialize failed for {endpoint}. Body: {content}", ex);
        }
    }

    // =========================
    // API #8 - Add Device
    // =========================
    public async Task<AddDeviceResp> AddDeviceAsync(IEnumerable<AddDeviceInfo> deviceInfos, CancellationToken ct = default)
    {
        var list = (deviceInfos ?? Enumerable.Empty<AddDeviceInfo>())
            .Where(x => x is not null
                        && !string.IsNullOrWhiteSpace(x.deviceGuid)
                        && !string.IsNullOrWhiteSpace(x.deviceName))
            .Select(x => new AddDeviceInfo(x.deviceName.Trim(), x.deviceGuid.Trim()))
            .ToList();

        if (list.Count == 0)
            throw new ArgumentException("deviceInfos is required and must not be empty.", nameof(deviceInfos));

        var resp = await PostAsync<AddDeviceResp>(
            endpoint: "/api/data-api/elitechAccess/addDevice",
            body: new
            {
                keyId = _opt.KeyId,
                keySecret = _opt.KeySecret,
                deviceInfos = list
            },
            auth: true,
            ct: ct);

        return resp ?? new AddDeviceResp { code = -1, message = "Empty response" };
    }

    // =========================
    // API #7 - Get Device GUIDs
    // =========================
    public async Task<DeviceGuidsResp> GetDeviceGuidsAsync(CancellationToken ct = default)
    {
        if (_cachedDeviceGuids != null &&
            DateTimeOffset.UtcNow - _deviceGuidsCachedAt < TimeSpan.FromSeconds(60))
        {
            return new DeviceGuidsResp
            {
                Code = 0,
                Message = "From cache",
                Data = _cachedDeviceGuids
            };
        }

        var resp = await PostAsync<DeviceGuidsResp>(
            endpoint: "/api/data-api/elitechAccess/getDeviceGuids",
            body: new
            {
                keyId = _opt.KeyId,
                keySecret = _opt.KeySecret
            },
            auth: true,
            ct: ct
        );

        if (resp == null)
        {
            return new DeviceGuidsResp
            {
                Code = -1,
                Message = "Empty response from Elitech"
            };
        }

        if (resp.Code == 0 && resp.Data != null)
        {
            _cachedDeviceGuids = resp.Data
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _deviceGuidsCachedAt = DateTimeOffset.UtcNow;

            resp.Data = _cachedDeviceGuids;
        }

        return resp;
    }
    // =========================
    // History V2 - i-elitech.com
    // =========================
    public async Task<HistoryResp> GetHistoryDataAsync(
        string deviceGuid,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceGuid))
            throw new ArgumentException("deviceGuid is required", nameof(deviceGuid));

        if (end < start)
            (start, end) = (end, start);

        // Tài liệu V2 chỉ hỗ trợ tối đa 10 ngày cho một request.
        if (end - start >= TimeSpan.FromDays(10))
        {
            throw new ArgumentException(
                "A single History V2 request must be shorter than 10 days. " +
                "Use GetHistoryDataRangeAsync for a longer range.");
        }

        var resp = await PostV2MappedAsync<HistoryResp>(
            endpoint: "/api/data-api/elitechAccess/v2/getHistoryData",
            body: new
            {
                keyId = _alarmOpt.KeyId,
                keySecret = _alarmOpt.KeySecret,
                deviceGuid = deviceGuid.Trim(),
                startTime = start.ToUnixTimeSeconds(),
                endTime = end.ToUnixTimeSeconds()
            },
            auth: true,
            ct: ct);

        return resp ?? new HistoryResp
        {
            code = -1,
            message = "Empty History V2 response"
        };
    }

    public async Task<HistoryResp> GetHistoryDataRangeAsync(
        string deviceGuid,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct = default,
        int delayBetweenRequestsMs = 0)
    {
        if (string.IsNullOrWhiteSpace(deviceGuid))
            throw new ArgumentException("deviceGuid is required", nameof(deviceGuid));

        if (end < start)
            (start, end) = (end, start);

        deviceGuid = deviceGuid.Trim();
        delayBetweenRequestsMs = Math.Max(0, delayBetweenRequestsMs);

        // API ghi tối đa 10 ngày. Trừ 1 giây để không chạm đúng biên 10 ngày.
        var maxSpan = TimeSpan.FromDays(10) - TimeSpan.FromSeconds(1);
        var chunks = BuildHistoryChunks(start, end, maxSpan);
        var all = new List<HistoryItem>();

        foreach (var (chunkStart, chunkEnd) in chunks)
        {
            ct.ThrowIfCancellationRequested();

            var resp = await GetHistoryChunkWithFallbackAsync(
                deviceGuid,
                chunkStart,
                chunkEnd,
                ct,
                delayBetweenRequestsMs);

            if (resp.code != 0)
                return resp;

            if (resp.data is { Count: > 0 })
                all.AddRange(resp.data);

            if (delayBetweenRequestsMs > 0)
                await Task.Delay(delayBetweenRequestsMs, ct);
        }

        var merged = all
            .Where(x => x != null)
            .GroupBy(x => new
            {
                DeviceGuid = x.deviceGuid?.Trim().ToUpperInvariant() ?? deviceGuid.ToUpperInvariant(),
                SubUid = x.subUid ?? -1,
                MonitorTime = x.monitorTime ?? 0L
            })
            .Select(g => g.First())
            .OrderBy(x => x.monitorTime ?? 0L)
            .ToList();

        return new HistoryResp
        {
            code = 0,
            message = $"Merged {merged.Count} samples from {chunks.Count} History V2 chunk(s)",
            data = merged
        };
    }

    private async Task<HistoryResp> GetHistoryChunkWithFallbackAsync(
        string deviceGuid,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct,
        int delayBetweenRequestsMs)
    {
        var resp = await GetHistoryDataAsync(deviceGuid, start, end, ct);

        if (resp.code == 0 || end - start <= TimeSpan.FromDays(1))
            return resp;

        // Một số thiết bị có thể trả lỗi khi khoảng dữ liệu quá dày.
        // Khi đó chia đôi chunk và thử lại, nhưng không chia nhỏ dưới 1 ngày.
        var halfTicks = (end - start).Ticks / 2;
        var middle = start.AddTicks(halfTicks);

        var left = await GetHistoryChunkWithFallbackAsync(
            deviceGuid, start, middle, ct, delayBetweenRequestsMs);

        if (left.code != 0)
            return left;

        if (delayBetweenRequestsMs > 0)
            await Task.Delay(delayBetweenRequestsMs, ct);

        var rightStart = middle.AddSeconds(1);
        if (rightStart > end)
            rightStart = end;

        var right = await GetHistoryChunkWithFallbackAsync(
            deviceGuid, rightStart, end, ct, delayBetweenRequestsMs);

        if (right.code != 0)
            return right;

        var merged = new List<HistoryItem>();
        if (left.data is { Count: > 0 }) merged.AddRange(left.data);
        if (right.data is { Count: > 0 }) merged.AddRange(right.data);

        return new HistoryResp
        {
            code = 0,
            message = "History V2 chunk was split and merged",
            data = merged
        };
    }

    private static List<(DateTimeOffset Start, DateTimeOffset End)> BuildHistoryChunks(
        DateTimeOffset start,
        DateTimeOffset end,
        TimeSpan maxSpan)
    {
        var chunks = new List<(DateTimeOffset Start, DateTimeOffset End)>();

        for (var current = start; current <= end;)
        {
            var chunkEnd = current + maxSpan;
            if (chunkEnd > end)
                chunkEnd = end;

            chunks.Add((current, chunkEnd));

            if (chunkEnd >= end)
                break;

            current = chunkEnd.AddSeconds(1);
        }

        return chunks;
    }

    // =========================
    // Realtime
    // =========================
    public async Task<RealTimeResp> GetRealTimeDataAsync(
    IEnumerable<string> deviceGuids,
    CancellationToken ct = default)
    {
        var arr = (deviceGuids ?? Enumerable.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(NormalizeGuid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (arr.Length == 0)
            throw new ArgumentException(
                "deviceGuids is required and must not be empty.",
                nameof(deviceGuids));

        var cacheKey = string.Join(",", arr);

        await _rtGate.WaitAsync(ct);

        try
        {
            var nowUtc = DateTime.UtcNow;

            // Cache đúng theo danh sách GUID.
            if (_rtCacheByKey.TryGetValue(cacheKey, out var cached))
            {
                var age = nowUtc - cached.HitUtc;

                if (age < _rtMinInterval)
                    return cached.Resp;
            }

            // Realtime dùng Standard API.
            // History / Params / AlarmRecord vẫn giữ V2 như cũ.
            var resp = await PostAsync<RealTimeResp>(
                endpoint: "/api/data-api/elitechAccess/getRealTimeData",
                body: new
                {
                    keyId = _opt.KeyId,
                    keySecret = _opt.KeySecret,
                    deviceGuids = arr
                },
                auth: true,
                ct: ct);

            var safeResp =
                resp ?? new RealTimeResp
                {
                    code = -1,
                    message = "Empty realtime response"
                };

            _rtCacheByKey[cacheKey] =
                (DateTime.UtcNow, safeResp);

            // Dọn cache cũ.
            if (_rtCacheByKey.Count > 200)
            {
                var cutoff =
                    DateTime.UtcNow - TimeSpan.FromMinutes(10);

                var oldKeys = _rtCacheByKey
                    .Where(x => x.Value.HitUtc < cutoff)
                    .Select(x => x.Key)
                    .ToList();

                foreach (var oldKey in oldKeys)
                    _rtCacheByKey.Remove(oldKey);
            }

            return safeResp;
        }
        finally
        {
            _rtGate.Release();
        }
    }

    public Task<RealTimeResp> GetRealTimeDataAsync(string deviceGuid, CancellationToken ct = default)
        => GetRealTimeDataAsync(new[] { deviceGuid }, ct);


    // =========================
    // Device Params V2 - i-elitech.com
    // POST /api/data-api/elitechAccess/v2/getParams
    // =========================
    public async Task<DeviceParamsResp> GetDeviceParamsAsync(
        IEnumerable<string> deviceGuids,
        CancellationToken ct = default)
    {
        var arr = (deviceGuids ?? Enumerable.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeGuid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (arr.Length == 0)
        {
            throw new ArgumentException(
                "deviceGuids is required and must not be empty.",
                nameof(deviceGuids));
        }

        var resp = await PostV2Async<DeviceParamsResp>(
            endpoint: "/api/data-api/elitechAccess/v2/getParams",
            body: new
            {
                keyId = _alarmOpt.KeyId,
                keySecret = _alarmOpt.KeySecret,
                deviceGuids = arr
            },
            auth: true,
            ct: ct);

        return resp ?? new DeviceParamsResp
        {
            code = -1,
            message = "Empty Device Params V2 response"
        };
    }

    // =========================
    // Device Info
    // =========================
    public async Task<DeviceInfoResp> GetDeviceInfoAsync(IEnumerable<string> deviceGuids, CancellationToken ct = default)
    {
        var arr = deviceGuids?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        if (arr.Length == 0)
            throw new ArgumentException("deviceGuids is required.", nameof(deviceGuids));

        var resp = await PostAsync<DeviceInfoResp>(
            endpoint: "/api/data-api/elitechAccess/getDeviceInfo",
            body: new { keyId = _opt.KeyId, keySecret = _opt.KeySecret, deviceGuids = arr },
            auth: true,
            ct: ct);

        return resp ?? new DeviceInfoResp { code = -1, message = "Empty response" };
    }

    // =========================================================
    // OPTIONAL: SetParam + BatchWaybill
    // Chỉ bật nếu bạn có model + namespace tương ứng
    // =========================================================

    public async Task<SetParamResp> SetParamAsync(SetParamReq req, CancellationToken ct = default)
    {
        if (req == null) throw new ArgumentNullException(nameof(req));
        if (string.IsNullOrWhiteSpace(req.deviceGuid))
            throw new ArgumentException("deviceGuid is required.", nameof(req));

        var body = new Dictionary<string, object?>
        {
            ["keyId"] = _opt.KeyId,
            ["keySecret"] = _opt.KeySecret,
            ["deviceGuid"] = req.deviceGuid,

            ["recordInterval"] = req.recordInterval,
            ["uploadInterval"] = req.uploadInterval,

            ["tmpUpper"] = req.tmpUpper,
            ["tmpLower"] = req.tmpLower,
            ["humUpper"] = req.humUpper,
            ["humLower"] = req.humLower,

            ["waybillStartTime"] = req.waybillStartTime,
            ["waybillNum"] = req.waybillNum,
        };

        var jsonOpts = new JsonSerializerOptions(_jsonOpts)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/api/data-api/elitechAccess/setParam");
        var token = await GetTokenAsync(ct);
        httpReq.Headers.Authorization = AuthenticationHeaderValue.Parse(token);
        httpReq.Content = new StringContent(JsonSerializer.Serialize(body, jsonOpts), Encoding.UTF8, "application/json");

        using var res = await _http.SendAsync(httpReq, ct);
        var content = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)res.StatusCode} setParam. Body: {content}");

        return JsonSerializer.Deserialize<SetParamResp>(content, _jsonOpts)
               ?? new SetParamResp { code = -1, message = "Empty response" };
    }

    public async Task<BatchWaybillResp> BatchSetWaybillAsync(
        IEnumerable<string> deviceGuids,
        long? start,
        long? stop,
        CancellationToken ct = default)
    {
        var arr = (deviceGuids ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (arr.Length == 0)
            throw new ArgumentException("deviceGuids is required.");
        if (arr.Length > 200)
            throw new ArgumentException("deviceGuids max 200.");
        if (start is null && stop is null)
            throw new ArgumentException("waybillStartTime or waybillStopTime is required.");

        var resp = await PostAsync<BatchWaybillResp>(
            endpoint: "/api/data-api/elitechAccess/batchSetWaybill",
            body: new
            {
                keyId = _opt.KeyId,
                keySecret = _opt.KeySecret,
                deviceGuids = arr,
                waybillStartTime = start,
                waybillStopTime = stop
            },
            auth: true,
            ct: ct);

        return resp ?? new BatchWaybillResp { code = -1, message = "Empty response" };
    }
    public async Task<AlarmRecordResp> GetAlarmRecordAsync(
      string deviceGuid,
      int subUid,
      DateTimeOffset from,
      DateTimeOffset to,
      CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceGuid))
            throw new ArgumentException("deviceGuid is required", nameof(deviceGuid));

        if (from > to) (from, to) = (to, from);

        var resp = await PostV2Async<AlarmRecordResp>(
            endpoint: "/api/data-api/elitechAccess/v2/getAlarmRecord",
            body: new
            {
                keyId = _alarmOpt.KeyId,
                keySecret = _alarmOpt.KeySecret,
                deviceGuid = deviceGuid.Trim(),
                subUid = subUid,
                startTime = from.ToUnixTimeSeconds(),
                endTime = to.ToUnixTimeSeconds()
            },
            auth: true,
            ct: ct);

        return resp ?? new AlarmRecordResp { code = -1, message = "Empty response" };
    }

    private async Task<string> SendV2RawAsync(
        string endpoint,
        object body,
        bool auth,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            endpoint);

        if (auth)
        {
            var token = await GetV2TokenAsync(ct);
            req.Headers.Authorization =
                AuthenticationHeaderValue.Parse(token);
        }

        req.Content = new StringContent(
            JsonSerializer.Serialize(body, _jsonOpts),
            Encoding.UTF8,
            "application/json");

        using var res = await _httpAlarm.SendAsync(req, ct);
        var content = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"HTTP {(int)res.StatusCode} when calling V2 " +
                $"{endpoint}. Body: {content}");
        }

        return content;
    }

    private async Task<T?> PostV2Async<T>(
        string endpoint,
        object body,
        bool auth,
        CancellationToken ct)
    {
        var content = await SendV2RawAsync(
            endpoint,
            body,
            auth,
            ct);

        try
        {
            return JsonSerializer.Deserialize<T>(
                content,
                _jsonOpts);
        }
        catch (Exception ex)
        {
            throw new JsonException(
                $"Deserialize failed for V2 {endpoint}. " +
                $"Body: {content}",
                ex);
        }
    }

    /// <summary>
    /// Dùng cho History/Realtime V2.
    /// Raw JSON được bổ sung field legacy từ Mongo trước khi deserialize,
    /// vì vậy mọi consumer cũ (chart, SMS, alarm) vẫn đọc tmp/hum như trước.
    /// </summary>
    private async Task<T?> PostV2MappedAsync<T>(
        string endpoint,
        object body,
        bool auth,
        CancellationToken ct)
    {
        var content = await SendV2RawAsync(
            endpoint,
            body,
            auth,
            ct);

        var enrichedContent =
            await _legacyProjection.EnrichResponseJsonAsync(
                content,
                ct);

        try
        {
            return JsonSerializer.Deserialize<T>(
                enrichedContent,
                _jsonOpts);
        }
        catch (Exception ex)
        {
            throw new JsonException(
                $"Deserialize mapped V2 response failed for " +
                $"{endpoint}. Body: {enrichedContent}",
                ex);
        }
    }

    public async Task<RealTimeResp> GetRealTimeDataBatchAsync(
    IEnumerable<string> deviceGuids,
    CancellationToken ct = default,
    int chunkSize = 200,
    int maxConcurrency = 4)
    {
        var chunks = ChunkGuids(deviceGuids, chunkSize);
        if (chunks.Count == 0)
            throw new ArgumentException("deviceGuids is required.", nameof(deviceGuids));

        // nếu chỉ 1 chunk thì dùng luôn Realtime V2 đã map legacy
        if (chunks.Count == 1)
            return await GetRealTimeDataAsync(chunks[0], ct);

        var sem = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = chunks.Select(async arr =>
        {
            await sem.WaitAsync(ct);
            try
            {
                return await GetRealTimeDataAsync(arr, ct);
            }
            finally { sem.Release(); }
        }).ToList();

        var results = await Task.WhenAll(tasks);

        // merge data
        var merged = new RealTimeResp { code = 0, message = $"Merged {results.Length} chunks" };
        var all = new List<RealTimeItem>(); // đổi RealTimeItem đúng theo model của bạn

        foreach (var r in results)
        {
            if (r == null) continue;
            if (r.code != 0) return r; // fail fast (hoặc bạn gom lỗi theo ý)
            if (r.data is { Count: > 0 }) all.AddRange(r.data);
        }

        // distinct theo (deviceGuid, monitorTime) hoặc (deviceGuid, lastSessionTime) tuỳ model
        merged.data = all
            .GroupBy(x => (x.deviceGuid ?? "", x.MonitorTime ?? 0L))
            .Select(g => g.First())
            .ToList();

        return merged;
    }


    private static List<string[]> ChunkGuids(IEnumerable<string> guids, int chunkSize)
    {
        var list = (guids ?? Enumerable.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeGuid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var chunks = new List<string[]>();
        for (int i = 0; i < list.Count; i += chunkSize)
            chunks.Add(list.Skip(i).Take(chunkSize).ToArray());

        return chunks;
    }

}