using System.Text.Json;
using System.Text.Json.Nodes;
using Elitech.Models.DeviceParams;

namespace Elitech.Services;

/// <summary>
/// Chuyển JSON V2 dạng paramCode/value thành các field legacy:
/// tmp1..tmp4, hum1..hum2, signal, power.
///
/// Service chỉ bổ sung field còn thiếu. Không ghi đè field cũ,
/// không thay đổi mảng data/params gốc và không chạm logic cảnh báo/chart.
/// </summary>
public class ElitechV2LegacyProjectionService
{
    private readonly ElitechDeviceParamMappingService _mappings;

    public ElitechV2LegacyProjectionService(
        ElitechDeviceParamMappingService mappings)
    {
        _mappings = mappings;
    }

    public async Task<string> EnrichResponseJsonAsync(
        string json,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(json))
            return json;

        JsonNode? root;

        try
        {
            root = JsonNode.Parse(json);
        }
        catch
        {
            // Việc chuẩn hóa không được phép làm request đang chạy bị hỏng.
            return json;
        }

        if (root == null)
            return json;

        var deviceGuids = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        CollectDeviceGuids(root, deviceGuids);

        if (deviceGuids.Count == 0)
            return json;

        var mappingRows = await _mappings.GetByDevicesAsync(
            deviceGuids,
            ct);

        var lookup = mappingRows
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.DeviceGuid) &&
                !string.IsNullOrWhiteSpace(x.ParamCode) &&
                !string.IsNullOrWhiteSpace(x.LegacyField))
            .GroupBy(
                x => BuildKey(
                    x.DeviceGuid,
                    x.SubUid,
                    x.ParamCode),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderByDescending(x =>
                        string.Equals(
                            x.MappingSource,
                            "MANUAL",
                            StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(x => x.UpdatedAtUtc)
                    .First()
                    .LegacyField!
                    .Trim()
                    .ToLowerInvariant(),
                StringComparer.OrdinalIgnoreCase);

        if (lookup.Count == 0)
            return json;

        EnrichNode(
            root,
            inheritedDeviceGuid: null,
            inheritedSubUid: 0,
            lookup);

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false
        });
    }

    private static void CollectDeviceGuids(
        JsonNode? node,
        HashSet<string> result)
    {
        if (node is JsonObject obj)
        {
            var guid = GetString(obj, "deviceGuid");

            if (!string.IsNullOrWhiteSpace(guid))
                result.Add(NormalizeGuid(guid));

            foreach (var pair in obj)
                CollectDeviceGuids(pair.Value, result);

            return;
        }

        if (node is JsonArray arr)
        {
            foreach (var child in arr)
                CollectDeviceGuids(child, result);
        }
    }

    private static void EnrichNode(
        JsonNode? node,
        string? inheritedDeviceGuid,
        int inheritedSubUid,
        IReadOnlyDictionary<string, string> lookup)
    {
        if (node is JsonObject obj)
        {
            var deviceGuid =
                NormalizeGuid(
                    GetString(obj, "deviceGuid") ??
                    inheritedDeviceGuid);

            var subUid =
                GetInt(obj, "subUid") ??
                inheritedSubUid;

            AddMonitorTimeAlias(obj);

            foreach (var pair in obj.ToList())
            {
                if (pair.Value is JsonArray arr &&
                    IsParameterArray(arr))
                {
                    ApplyParameterArray(
                        obj,
                        arr,
                        deviceGuid,
                        subUid,
                        lookup);
                }
            }

            foreach (var pair in obj.ToList())
            {
                EnrichNode(
                    pair.Value,
                    deviceGuid,
                    subUid,
                    lookup);
            }

            return;
        }

        if (node is JsonArray children)
        {
            foreach (var child in children)
            {
                EnrichNode(
                    child,
                    inheritedDeviceGuid,
                    inheritedSubUid,
                    lookup);
            }
        }
    }

    private static bool IsParameterArray(JsonArray arr)
    {
        if (arr.Count == 0)
            return false;

        foreach (var child in arr)
        {
            if (child is JsonObject param &&
                !string.IsNullOrWhiteSpace(
                    GetString(param, "paramCode")))
            {
                return true;
            }
        }

        return false;
    }

    private static void ApplyParameterArray(
        JsonObject owner,
        JsonArray parameters,
        string deviceGuid,
        int subUid,
        IReadOnlyDictionary<string, string> lookup)
    {
        if (string.IsNullOrWhiteSpace(deviceGuid))
            return;

        foreach (var child in parameters)
        {
            if (child is not JsonObject param)
                continue;

            var paramCode =
                NormalizeCode(
                    GetString(param, "paramCode"));

            if (string.IsNullOrWhiteSpace(paramCode))
                continue;

            var key = BuildKey(
                deviceGuid,
                subUid,
                paramCode);

            if (!lookup.TryGetValue(key, out var legacyField))
            {
                // Một số response không trả subUid dù getParams lưu ở subUid=0.
                var fallbackKey = BuildKey(
                    deviceGuid,
                    0,
                    paramCode);

                lookup.TryGetValue(
                    fallbackKey,
                    out legacyField);
            }

            if (string.IsNullOrWhiteSpace(legacyField))
                continue;

            var value =
                GetNode(param, "value") ??
                GetNode(param, "paramValue") ??
                GetNode(param, "paramDefault");

            if (value == null || IsJsonNull(value))
                continue;

            if (!ShouldFill(owner, legacyField))
                continue;

            owner[legacyField] = CloneNode(value);
        }
    }

    private static void AddMonitorTimeAlias(JsonObject obj)
    {
        if (!ShouldFill(obj, "monitorTime"))
            return;

        var value =
            GetNode(obj, "monitorTimeStamp") ??
            GetNode(obj, "MonitorTimeStamp");

        if (value != null && !IsJsonNull(value))
            obj["monitorTime"] = CloneNode(value);
    }

    private static bool ShouldFill(
        JsonObject obj,
        string propertyName)
    {
        var existing = GetNode(obj, propertyName);

        if (existing == null || IsJsonNull(existing))
            return true;

        if (existing is JsonValue value &&
            value.TryGetValue<string>(out var text))
        {
            return string.IsNullOrWhiteSpace(text);
        }

        return false;
    }

    private static JsonNode? GetNode(
        JsonObject obj,
        string propertyName)
    {
        foreach (var pair in obj)
        {
            if (string.Equals(
                pair.Key,
                propertyName,
                StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static string? GetString(
        JsonObject obj,
        string propertyName)
    {
        var node = GetNode(obj, propertyName);

        if (node is not JsonValue value)
            return null;

        if (value.TryGetValue<string>(out var text))
            return text;

        return node.ToJsonString().Trim('"');
    }

    private static int? GetInt(
        JsonObject obj,
        string propertyName)
    {
        var node = GetNode(obj, propertyName);

        if (node is not JsonValue value)
            return null;

        if (value.TryGetValue<int>(out var i))
            return i;

        if (value.TryGetValue<long>(out var l) &&
            l >= int.MinValue &&
            l <= int.MaxValue)
        {
            return (int)l;
        }

        if (value.TryGetValue<string>(out var text) &&
            int.TryParse(text, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool IsJsonNull(JsonNode node)
    {
        return string.Equals(
            node.ToJsonString(),
            "null",
            StringComparison.OrdinalIgnoreCase);
    }

    private static JsonNode? CloneNode(JsonNode node)
    {
        return JsonNode.Parse(node.ToJsonString());
    }

    private static string BuildKey(
        string? deviceGuid,
        int subUid,
        string? paramCode)
    {
        return $"{NormalizeGuid(deviceGuid)}|{subUid}|{NormalizeCode(paramCode)}";
    }

    private static string NormalizeGuid(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? ""
            : value.Trim().ToUpperInvariant();

    private static string NormalizeCode(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? ""
            : value.Trim().ToUpperInvariant();
}