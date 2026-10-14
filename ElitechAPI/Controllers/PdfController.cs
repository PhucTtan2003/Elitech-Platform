using Microsoft.AspNetCore.Mvc;
using DinkToPdf;
using DinkToPdf.Contracts;

namespace Elitech.Controllers
{
    [Route("export")]
    public class PdfController : Controller
    {
        private readonly IConverter _converter;

        public PdfController(IConverter converter)
        {
            _converter = converter;
        }

        [HttpPost("pdf")]
        public IActionResult ExportPdf([FromBody] ExportPdfRequest req)
        {
            var baseUrl = $"{Request.Scheme}://{Request.Host}";

            // Giờ Việt Nam, tránh trường hợp server chạy giờ UTC
            var exportAt = DateTime.UtcNow.AddHours(7);

            // AUTO FIX: thêm class data-table nếu chưa có
            var tableHtml = req.TableHtml ?? "";

            if (!tableHtml.Contains("data-table"))
            {
                tableHtml = tableHtml.Replace("<table", "<table class='data-table'");
            }

            var rangeHtml = "";

            if (!string.IsNullOrEmpty(req.From))
            {
                rangeHtml = $"<div>Khoảng: {req.From} → {req.To}</div>";
            }

            var chartHtml = "";

            if (!string.IsNullOrEmpty(req.ChartImage))
            {
                chartHtml = $@"
            <div class='section-title'>Biểu đồ</div>
            <img class='chart' src='{req.ChartImage}' />";
            }

            var html = $@"
            <html>
            <head>
            <meta charset='utf-8'>
            <style>
            body {{
            font-family: Arial;
            padding: 20px;
            color: #111827;
             }}

    /* ===== TABLE GLOBAL ===== */
            table {{
            width: 100%;
            border-collapse: collapse;
            margin-top: 10px;
            font-size: 12px;
             }}

    /* ===== CHỈ BẢNG DỮ LIỆU CÓ BORDER ===== */
            .data-table th,
            .data-table td {{
            border: 1px solid #333;
            padding: 6px;
            text-align: center;
    }}

            .data-table th {{
             background: #f3f4f6;
             font-weight: bold;
    }}

    /* ===== HEADER KHÔNG VIỀN ===== */
    .header-table {{
        width: 100%;
        margin-bottom: 20px;
    }}

    .header-table td {{
        border: none;
    }}

    .logo {{
        width: 100px;
    }}

    .company-info {{
        font-size: 12px;
        line-height: 1.6;
        text-align: center;
    }}

    .company-name {{
        font-weight: bold;
        font-size: 20px;
        color: #0D47A1;
        letter-spacing: 0.5px;
    }}

    .report-title {{
        font-size: 15px;
        font-weight: bold;
        margin-top: 4px;
    }}

    /* ===== SECTION ===== */
    .section-title {{
        font-size: 16px;
        font-weight: bold;
        margin: 20px 0 10px;
        color: #0D47A1;
    }}

    img.chart {{
        width: 100%;
        margin-top: 10px;
    }}
</style>
</head>

<body>

<!-- HEADER -->
<table class='header-table'>
    <tr>
        <td style='width:120px; vertical-align:top;'>
            <img class='logo' src='{baseUrl}/Pictures/elitechlogo2.png' />
        </td>

        <td class='company-info'>
            <div class='company-name'>ELITECH VIETNAM</div>
            <div class='report-title'>Báo cáo dữ liệu thiết bị</div>
            <div>Xuất lúc: {exportAt:dd/MM/yyyy HH:mm}</div>
            {rangeHtml}
        </td>

        <td style='width:120px;'></td>
    </tr>
</table>

<!-- CHART -->
{chartHtml}

<!-- TABLE -->
<div class='section-title'>Bảng dữ liệu</div>
{tableHtml}

</body>
</html>";

            var doc = new HtmlToPdfDocument()
            {
                GlobalSettings =
                {
                    PaperSize = PaperKind.A4,
                    Orientation = Orientation.Portrait
                },
                Objects =
                {
                    new ObjectSettings()
                    {
                        HtmlContent = html,
                        WebSettings =
                        {
                            DefaultEncoding = "utf-8",
                            LoadImages = true
                        }
                    }
                }
            };

            var pdf = _converter.Convert(doc);

            // Không dùng dấu / hoặc : trong tên file vì Windows dễ lỗi
            var fileName = $"Elitech VietNam - {exportAt:dd-MM-yyyy HH-mm}.pdf";

            return File(pdf, "application/pdf", fileName);
        }
    }

    public class ExportPdfRequest
    {
        public string? ChartImage { get; set; }
        public string? TableHtml { get; set; }
        public string? From { get; set; }
        public string? To { get; set; }
    }
}