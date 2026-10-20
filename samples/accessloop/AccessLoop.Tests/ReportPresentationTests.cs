using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class ReportPresentationTests
{
    [TestMethod]
    [DataRow(-1L, "未知（无效用时）")]
    [DataRow(0L, "0 秒")]
    [DataRow(200L, "不足 1 秒")]
    [DataRow(1200L, "1 秒")]
    [DataRow(59500L, "1 分 0 秒")]
    [DataRow(75105L, "1 分 15 秒")]
    [DataRow(938104L, "15 分 38 秒")]
    [DataRow(1013209L, "16 分 53 秒")]
    [DataRow(3600000L, "1 小时 0 分 0 秒")]
    public void PrimaryDurationUsesReadableUnits(long milliseconds, string expected) =>
        Assert.AreEqual(expected, ReportPresentation.Duration(milliseconds));

    [TestMethod]
    public void DisclosureLabelIsEscapedAndClosedByDefault()
    {
        var html = new StringBuilder();
        ReportPresentation.Details(html, "<script>label</script>", () => html.Append("<p>Evidence</p>"));
        Assert.AreEqual("<details class=\"report-detail\"><summary>&lt;script&gt;label&lt;/script&gt;</summary><p>Evidence</p></details>", html.ToString());
    }
}
