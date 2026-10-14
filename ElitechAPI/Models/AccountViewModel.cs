using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System;

namespace Elitech.Models
{
    [BsonIgnoreExtraElements] // chống crash khi thêm field sau này
    public class AccountViewModel
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; } = default!;

        [BsonElement("username")]
        public string Username { get; set; } = string.Empty;

        [BsonElement("passwordHash")]
        public string PasswordHash { get; set; } = string.Empty;

        [BsonElement("role")]
        public RoleViewModel Role { get; set; } = RoleViewModel.User;

        [BsonElement("isActive")]
        public bool IsActive { get; set; } = true;

        [BsonElement("createdAtUtc")]
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        // ===== PROFILE =====
        [BsonElement("fullName")]
        public string? FullName { get; set; }

        [BsonElement("email")]
        public string? Email { get; set; }

        [BsonElement("phone")]
        public string? Phone { get; set; }   // ✅ SỐ CHÍNH (bắt buộc giữ)

        [BsonElement("phones")]
        public List<string> Phones { get; set; } = new();

        // ✅ verify state
        [BsonElement("phoneVerified")]
        public bool PhoneVerified { get; set; } = false; // (đoạn 1 có default)

        [BsonElement("phoneVerifiedAtUtc")]
        public DateTime? PhoneVerifiedAtUtc { get; set; }

        [BsonElement("phoneVerifyCodeHash")]
        public string? PhoneVerifyCodeHash { get; set; }

        [BsonElement("phoneVerifyExpiresAtUtc")]
        public DateTime? PhoneVerifyExpiresAtUtc { get; set; }

        [BsonElement("phoneVerifyLastSentAtUtc")]
        public DateTime? PhoneVerifyLastSentAtUtc { get; set; }

        // ===== SMS SUBSCRIPTION =====
        [BsonElement("smsAlertEnabled")]
        public bool SmsAlertEnabled { get; set; } = false; // (đoạn 1 có default)

        [BsonElement("smsSubActive")]
        public bool SmsSubActive { get; set; } = false;

        [BsonElement("smsSubStartedAtUtc")]
        public DateTime? SmsSubStartedAtUtc { get; set; }

        [BsonElement("smsSubExpiresAtUtc")]
        public DateTime? SmsSubExpiresAtUtc { get; set; }

        [BsonElement("smsSelfRegisterAllowed")]
        public bool SmsSelfRegisterAllowed { get; set; } = true; // giữ "cũ" của đoạn 2 (không đổi)

        //[BsonElement("avatarUrl")]
        //public string? AvatarUrl { get; set; }
    }
}