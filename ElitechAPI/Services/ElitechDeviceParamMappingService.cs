using System.Text.Json;
using System.Text.RegularExpressions;
using Elitech.Data;
using Elitech.Models.DeviceParams;
using MongoDB.Driver;

namespace Elitech.Services;

public class ElitechDeviceParamMappingService
{
    private readonly IMongoCollection<ElitechDeviceParamMapping> _col;

    private static readonly HashSet<string> AllowedLegacyFields =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "tmp1", "tmp2", "tmp3", "tmp4",
            "hum1", "hum2",
            "signal", "power"
        };

    public ElitechDeviceParamMappingService(MongoContext ctx)
    {
        _col = ctx.Database.GetCollection<ElitechDeviceParamMapping>(
            "DeviceParamMappings");

        EnsureIndexes();
    }

    private static string NormalizeGuid(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? ""
            : value.Trim().ToUpperInvariant();

    private static string NormalizeCode(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? ""
            : value.Trim().ToUpperInvariant();

    private void EnsureIndexes()
    {
        var uniqueKeys = Builders<ElitechDeviceParamMapping>.IndexKeys
            .Ascending(x => x.DeviceGuid)
            .Ascending(x => x.SubUid)
            .Ascending(x => x.ParamCode);

        _col.Indexes.CreateOne(
            new CreateIndexModel<ElitechDeviceParamMapping>(
                uniqueKeys,
                new CreateIndexOptions
                {
                    Unique = true,
                    Name = "ux_device_subuid_paramcode"
                }));

        _col.Indexes.CreateOne(
            new CreateIndexModel<ElitechDeviceParamMapping>(
                Builders<ElitechDeviceParamMapping>.IndexKeys
                    .Ascending(x => x.DeviceGuid)
                    .Ascending(x => x.SubUid),
                new CreateIndexOptions
                {
                    Name = "ix_device_subuid"
                }));

        _col.Indexes.CreateOne(
            new CreateIndexModel<ElitechDeviceParamMapping>(
                Builders<ElitechDeviceParamMapping>.IndexKeys
                    .Ascending(x => x.LegacyField),
                new CreateIndexOptions
                {
                    Name = "ix_legacy_field"
                }));
    }

    public async Task<int> UpsertFromApiAsync(
        DeviceParamsResp response,
        CancellationToken ct = default)
    {
        if (response.code != 0 || response.data == null)
            return 0;

        var saved = 0;

        foreach (var device in response.data)
        {
            var guid = NormalizeGuid(device.deviceGuid);
            if (string.IsNullOrWhiteSpace(guid))
                continue;

            var subUid = device.subUid ?? 0;
            var parameters = (device.@params ?? new List<DeviceParamItem>())
                .Where(x => !string.IsNullOrWhiteSpace(x.paramCode))
                .ToList();

            var autoMapping = BuildAutoLegacyMapping(parameters);

            foreach (var param in parameters)
            {
                ct.ThrowIfCancellationRequested();

                var code = NormalizeCode(param.paramCode);
                if (string.IsNullOrWhiteSpace(code))
                    continue;

                var filter = Builders<ElitechDeviceParamMapping>.Filter.Where(
                    x => x.DeviceGuid == guid &&
                         x.SubUid == subUid &&
                         x.ParamCode == code);

                var existing = await _col
                    .Find(filter)
                    .FirstOrDefaultAsync(ct);

                var isManual = string.Equals(
                    existing?.MappingSource,
                    "MANUAL",
                    StringComparison.OrdinalIgnoreCase);

                var legacyField = isManual
                    ? existing?.LegacyField
                    : autoMapping.GetValueOrDefault(code);

                var mappingSource = isManual
                    ? "MANUAL"
                    : string.IsNullOrWhiteSpace(legacyField)
                        ? "UNMAPPED"
                        : "AUTO";

                var now = DateTime.UtcNow;

                var update = Builders<ElitechDeviceParamMapping>.Update
                    .Set(x => x.ParamName, Clean(param.paramName))
                    .Set(x => x.UnitCode, Clean(param.unitCode))
                    .Set(x => x.DataType, param.dataType)
                    .Set(x => x.ReadWriteType, param.readWriteType)
                    .Set(x => x.ValueRange, Clean(param.valueRange))
                    .Set(x => x.PrecisionUnit, param.precisionUnit)
                    .Set(x => x.ParamDefault, Clean(param.paramDefault))
                    .Set(x => x.ParamDescription, Clean(param.paramDescription))
                    .Set(x => x.BitLength, param.bitLength)
                    .Set(x => x.BitValueJson, ToJson(param.bitValue))
                    .Set(x => x.BitLabelJson, ToJson(param.bitLabel))
                    .Set(x => x.LegacyField, legacyField)
                    .Set(x => x.MappingSource, mappingSource)
                    .Set(x => x.ApiVersion, "v2")
                    .Set(x => x.UpdatedAtUtc, now)
                    .SetOnInsert(x => x.DeviceGuid, guid)
                    .SetOnInsert(x => x.SubUid, subUid)
                    .SetOnInsert(x => x.ParamCode, code)
                    .SetOnInsert(x => x.CreatedAtUtc, now);

                await _col.UpdateOneAsync(
                    filter,
                    update,
                    new UpdateOptions { IsUpsert = true },
                    ct);

                saved++;
            }
        }

        return saved;
    }


    /// <summary>
    /// Đọc mapping của nhiều thiết bị trong một query Mongo.
    /// Chỉ lấy các document có LegacyField để phục vụ History/Realtime.
    /// </summary>
    public Task<List<ElitechDeviceParamMapping>> GetByDevicesAsync(
        IEnumerable<string> deviceGuids,
        CancellationToken ct = default)
    {
        var guids = (deviceGuids ?? Enumerable.Empty<string>())
            .Select(NormalizeGuid)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (guids.Count == 0)
            return Task.FromResult(
                new List<ElitechDeviceParamMapping>());

        var filter =
            Builders<ElitechDeviceParamMapping>.Filter.In(
                x => x.DeviceGuid,
                guids) &
            Builders<ElitechDeviceParamMapping>.Filter.Ne(
                x => x.LegacyField,
                null) &
            Builders<ElitechDeviceParamMapping>.Filter.Ne(
                x => x.LegacyField,
                "");

        return _col.Find(filter)
            .ToListAsync(ct);
    }

    public Task<List<ElitechDeviceParamMapping>> GetByDeviceAsync(
        string deviceGuid,
        int? subUid = null,
        CancellationToken ct = default)
    {
        var guid = NormalizeGuid(deviceGuid);

        var filter = subUid.HasValue
            ? Builders<ElitechDeviceParamMapping>.Filter.Where(
                x => x.DeviceGuid == guid && x.SubUid == subUid.Value)
            : Builders<ElitechDeviceParamMapping>.Filter.Where(
                x => x.DeviceGuid == guid);

        return _col.Find(filter)
            .SortBy(x => x.SubUid)
            .ThenBy(x => x.ParamCode)
            .ToListAsync(ct);
    }

    public Task<ElitechDeviceParamMapping?> GetOneAsync(
        string deviceGuid,
        int subUid,
        string paramCode,
        CancellationToken ct = default)
    {
        var guid = NormalizeGuid(deviceGuid);
        var code = NormalizeCode(paramCode);

        return _col.Find(x =>
                x.DeviceGuid == guid &&
                x.SubUid == subUid &&
                x.ParamCode == code)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> SetManualLegacyFieldAsync(
        string deviceGuid,
        int subUid,
        string paramCode,
        string? legacyField,
        CancellationToken ct = default)
    {
        var guid = NormalizeGuid(deviceGuid);
        var code = NormalizeCode(paramCode);

        legacyField = string.IsNullOrWhiteSpace(legacyField)
            ? null
            : legacyField.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(guid))
            throw new ArgumentException("deviceGuid is required");

        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("paramCode is required");

        if (legacyField != null && !AllowedLegacyFields.Contains(legacyField))
        {
            throw new ArgumentException(
                "legacyField must be tmp1..tmp4, hum1..hum2, signal, power or null");
        }

        var filter = Builders<ElitechDeviceParamMapping>.Filter.Where(
            x => x.DeviceGuid == guid &&
                 x.SubUid == subUid &&
                 x.ParamCode == code);

        var update = Builders<ElitechDeviceParamMapping>.Update
            .Set(x => x.LegacyField, legacyField)
            .Set(x => x.MappingSource, "MANUAL")
            .Set(x => x.UpdatedAtUtc, DateTime.UtcNow);

        var result = await _col.UpdateOneAsync(
            filter,
            update,
            cancellationToken: ct);

        return result.MatchedCount > 0;
    }

    private static Dictionary<string, string> BuildAutoLegacyMapping(
        List<DeviceParamItem> parameters)
    {
        var result = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        var temperatures = parameters
            .Where(IsTemperature)
            .OrderBy(GetNaturalRank)
            .ThenBy(x => NormalizeCode(x.paramCode))
            .Take(4)
            .ToList();

        for (var i = 0; i < temperatures.Count; i++)
        {
            var code = NormalizeCode(temperatures[i].paramCode);
            if (!string.IsNullOrWhiteSpace(code))
                result[code] = $"tmp{i + 1}";
        }

        var humidities = parameters
            .Where(IsHumidity)
            .OrderBy(GetNaturalRank)
            .ThenBy(x => NormalizeCode(x.paramCode))
            .Take(2)
            .ToList();

        for (var i = 0; i < humidities.Count; i++)
        {
            var code = NormalizeCode(humidities[i].paramCode);
            if (!string.IsNullOrWhiteSpace(code))
                result[code] = $"hum{i + 1}";
        }

        foreach (var param in parameters)
        {
            var code = NormalizeCode(param.paramCode);
            if (string.IsNullOrWhiteSpace(code) || result.ContainsKey(code))
                continue;

            var signature = BuildSignature(param);

            if (Regex.IsMatch(
                    signature,
                    @"\b(signal|rssi|gsm signal|wifi signal|tín hiệu|tin hieu)\b",
                    RegexOptions.IgnoreCase))
            {
                result[code] = "signal";
                continue;
            }

            if (Regex.IsMatch(
                    signature,
                    @"\b(power|battery|battery level|pin|nguồn|nguon)\b",
                    RegexOptions.IgnoreCase))
            {
                result[code] = "power";
            }
        }

        return result;
    }

    private static bool IsTemperature(DeviceParamItem param)
    {
        var unit = (param.unitCode ?? "").Trim().ToUpperInvariant();
        var signature = BuildSignature(param);

        return unit.Contains("℃") ||
               unit.Contains("°C") ||
               unit.Contains("℉") ||
               unit.Contains("°F") ||
               Regex.IsMatch(
                   signature,
                   @"\b(temperature|temp|probe|nhiệt độ|nhiet do)\b",
                   RegexOptions.IgnoreCase);
    }

    private static bool IsHumidity(DeviceParamItem param)
    {
        var unit = (param.unitCode ?? "").Trim().ToUpperInvariant();
        var signature = BuildSignature(param);

        return unit.Contains("%RH") ||
               string.Equals(unit, "RH", StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(
                   signature,
                   @"\b(humidity|humid|độ ẩm|do am)\b",
                   RegexOptions.IgnoreCase);
    }

    private static string BuildSignature(DeviceParamItem param)
        => $"{param.paramName} {param.unitCode} {param.paramDescription}"
            .Trim()
            .ToLowerInvariant();

    private static int GetNaturalRank(DeviceParamItem param)
    {
        var source = $"{param.paramName} {param.paramCode}";
        var match = Regex.Match(source, @"\d+");

        return match.Success && int.TryParse(match.Value, out var value)
            ? value
            : int.MaxValue;
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ToJson(JsonElement? value)
    {
        if (!value.HasValue)
            return null;

        var element = value.Value;

        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        return element.GetRawText();
    }
}