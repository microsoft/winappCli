using System.IO;
using System.Text.Json;
using System.Windows;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace PingNotifier;

public partial class App : Application
{
    // Startup diagnostics, shared with support: C:\ProgramData\PingNotifier\diagnostics.json
    private static readonly string DiagnosticsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PingNotifier", "diagnostics.json");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var diag = new Dictionary<string, object?>
        {
            ["time"] = DateTime.UtcNow.ToString("o"),
            ["processPath"] = Environment.ProcessPath,
        };
        try
        {
            diag["packageFamilyName"] = global::Windows.ApplicationModel.Package.Current.Id.FamilyName;
            diag["hasIdentity"] = true;
        }
        catch (Exception ex)
        {
            diag["hasIdentity"] = false;
            diag["identityError"] = ex.Message;
        }

        try
        {
            ShowToast("PingNotifier is running", "You'll get a notification when a monitored host goes down.");
            diag["toastShown"] = true;
        }
        catch (Exception ex)
        {
            diag["toastShown"] = false;
            diag["toastError"] = $"{ex.GetType().Name}: {ex.Message}";
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DiagnosticsPath)!);
            File.WriteAllText(DiagnosticsPath, JsonSerializer.Serialize(diag, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static void ShowToast(string title, string body)
    {
        XmlDocument xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
        var texts = xml.GetElementsByTagName("text");
        texts[0].AppendChild(xml.CreateTextNode(title));
        texts[1].AppendChild(xml.CreateTextNode(body));
        ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(xml));
    }
}
