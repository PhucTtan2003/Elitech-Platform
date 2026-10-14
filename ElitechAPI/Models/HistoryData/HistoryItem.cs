using System.Text.Json.Serialization;

namespace Elitech.Models.HistoryData
{
    public class HistoryItem
    {
        public string? deviceGuid { get; set; }
        public int? subUid { get; set; }

        // Giữ field monitorTime để các Controller và JavaScript/cshtml V1 không bị vỡ.
        public long? monitorTime { get; set; }

        // Elitech V2 trả về monitorTimeStamp.
        // Setter đồng bộ ngược về monitorTime; khi trả JSON sẽ có cả hai field để tương thích.
        [JsonPropertyName("monitorTimeStamp")]
        public long? monitorTimeStamp
        {
            get => monitorTime;
            set => monitorTime = value;
        }

        public string? monitorTimeStr { get; set; }
        public string? position { get; set; }
        public string? address { get; set; }

        // Dữ liệu chuẩn của History V2: paramCode/value/unitCode/paramName.
        public List<HistoryParamItem>? data { get; set; }

        // Giữ các field phẳng cũ để code/cshtml hiện tại vẫn compile.
        // Việc map paramCode sang tmp/hum sẽ làm sau khi có bảng mã chính thức.
        public string? tmp1 { get; set; }
        public string? tmp2 { get; set; }
        public string? tmp3 { get; set; }
        public string? tmp4 { get; set; }
        public string? hum1 { get; set; }
        public string? hum2 { get; set; }
        public string? lux1 { get; set; }
        public string? power { get; set; }
        public string? signal { get; set; }
    }

    public class HistoryParamItem
    {
        public string? paramCode { get; set; }
        public string? value { get; set; }
        public string? unitCode { get; set; }
        public string? paramName { get; set; }
    }
}