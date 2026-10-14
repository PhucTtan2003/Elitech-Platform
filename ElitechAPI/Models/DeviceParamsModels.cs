using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Elitech.Models.DeviceParams;

// =========================================================
// Response từ Elitech Cloud V2: /v2/getParams
// =========================================================
public class DeviceParamsResp
{
    public int code { get; set; }
    public string? message { get; set; }
    public string? msg { get; set; }
    public string? error { get; set; }
    public string? time { get; set; }
    public List<DeviceParamsDeviceItem>? data { get; set; }
}

public class DeviceParamsDeviceItem
{
    public string? deviceGuid { get; set; }
    public int? subUid { get; set; }
    public List<DeviceParamItem>? @params { get; set; }
}

public class DeviceParamItem
{
    public string? paramCode { get; set; }
    public string? paramName { get; set; }
    public int? dataType { get; set; }
    public string? unitCode { get; set; }
    public int? readWriteType { get; set; }
    public string? valueRange { get; set; }
    public JsonElement? bitValue { get; set; }
    public int? precisionUnit { get; set; }
    public string? paramDefault { get; set; }
    public string? paramDescription { get; set; }
    public int? bitLength { get; set; }
    public JsonElement? bitLabel { get; set; }
}

// =========================================================
// Document Mongo mới, nằm chung database ElitechAuth.
// Collection: DeviceParamMappings
//
// Không sửa document V1 trong Devices, AlertRules, AlertStates,
// AlertEvents hoặc DeviceAssignments.
// =========================================================
public class ElitechDeviceParamMapping
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    public string DeviceGuid { get; set; } = "";
    public int SubUid { get; set; }
    public string ParamCode { get; set; } = "";

    public string? ParamName { get; set; }
    public string? UnitCode { get; set; }
    public int? DataType { get; set; }
    public int? ReadWriteType { get; set; }
    public string? ValueRange { get; set; }
    public int? PrecisionUnit { get; set; }
    public string? ParamDefault { get; set; }
    public string? ParamDescription { get; set; }
    public int? BitLength { get; set; }
    public string? BitValueJson { get; set; }
    public string? BitLabelJson { get; set; }

    // Field tương thích với logic V1:
    // tmp1..tmp4, hum1..hum2, signal, power hoặc null.
    public string? LegacyField { get; set; }

    // AUTO: suy ra từ paramName/unitCode.
    // MANUAL: Admin chỉnh bằng API, lần đồng bộ sau không ghi đè.
    // UNMAPPED: chưa xác định được.
    public string MappingSource { get; set; } = "UNMAPPED";

    public string ApiVersion { get; set; } = "v2";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}