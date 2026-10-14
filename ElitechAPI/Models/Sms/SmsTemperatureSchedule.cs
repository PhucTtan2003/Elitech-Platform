using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Elitech.Models.Sms;

public class SmsTemperatureSchedule
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    public string UserId { get; set; } = "";

    public string DeviceGuid { get; set; } = "";
    public string? DeviceName { get; set; }

    // Field cũ: giữ lại để không vỡ dữ liệu cũ.
    // Nên lưu số đầu tiên trong PhoneNumbers vào đây.
    public string Phone { get; set; } = "";

    // Field mới: danh sách số nhận SMS.
    // Ví dụ: ["84836093757", "84859776985"]
    [BsonElement("phoneNumbers")]
    public List<string> PhoneNumbers { get; set; } = new();

    // Ví dụ: ["07:00", "15:00"]
    public List<string> ScheduleTimes { get; set; } = new();

    public bool Enabled { get; set; } = true;

    // Linux dùng Asia/Ho_Chi_Minh, Windows nếu lỗi thì fallback Local
    public string TimeZoneId { get; set; } = "Asia/Ho_Chi_Minh";

    // Chống gửi trùng trong cùng 1 ngày + cùng 1 mốc giờ
    // key dạng: 20260603|07:00
    public List<string> ClaimedKeys { get; set; } = new();
    public List<string> SentKeys { get; set; } = new();

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastClaimedAtUtc { get; set; }
    public DateTime? LastSentAtUtc { get; set; }

    // Helper để backend/worker lấy danh sách số gửi.
    // Nếu dữ liệu cũ chưa có PhoneNumbers thì fallback sang Phone.
    [BsonIgnore]
    public List<string> EffectivePhones
    {
        get
        {
            var phones = PhoneNumbers != null && PhoneNumbers.Count > 0
                ? PhoneNumbers
                : new List<string> { Phone };

            return phones
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}

public class SmsTemperatureSendLog
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    public string ScheduleId { get; set; } = "";
    public string UserId { get; set; } = "";

    public string DeviceGuid { get; set; } = "";
    public string? DeviceName { get; set; }

    // Log này là log theo từng số gửi.
    // Nếu 1 lịch có 2 số thì sẽ tạo 2 log, mỗi log 1 Phone.
    public string Phone { get; set; } = "";

    public string DueKey { get; set; } = "";
    public string DueTime { get; set; } = "";

    public string? RequestId { get; set; }
    public string? Message { get; set; }

    public bool Ok { get; set; }
    public string? RawResponse { get; set; }
    public string? Error { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}