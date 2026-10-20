using System.Globalization;
using System.Net;
using System.Text;

namespace AccessLoop;

internal static class ReportPresentation
{
    public static void Details(StringBuilder html, string label, Action content)
    {
        html.Append("<details class=\"report-detail\"><summary>")
            .Append(WebUtility.HtmlEncode(label)).Append("</summary>");
        content();
        html.Append("</details>");
    }

    public static string Duration(decimal milliseconds)
    {
        if (milliseconds < 0)
            return "未知（无效用时）";
        if (milliseconds is > 0 and < 1000)
            return "不足 1 秒";
        var seconds = decimal.Round(milliseconds / 1000, 0, MidpointRounding.AwayFromZero);
        var hours = decimal.Floor(seconds / 3600);
        var minutes = decimal.Floor(seconds % 3600 / 60);
        var remainder = seconds % 60;
        return hours > 0
            ? $"{N(hours)} 小时 {N(minutes)} 分 {N(remainder)} 秒"
            : minutes > 0 ? $"{N(minutes)} 分 {N(remainder)} 秒" : $"{N(remainder)} 秒";
    }

    public static string CaptureStatus(string status) => status switch
    {
        "completed" => "采集完成",
        "partial" => "部分采集",
        "blocked" => "采集受阻",
        _ => "未知采集状态：" + status
    };

    private static string N(decimal value) => value.ToString("0", CultureInfo.InvariantCulture);
}
