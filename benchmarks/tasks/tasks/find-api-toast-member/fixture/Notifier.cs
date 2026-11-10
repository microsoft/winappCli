using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Reminders;

public static class Notifier
{
    public static void ShowReminder(string title, string body, TimeSpan lifetime)
    {
        XmlDocument xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
        var texts = xml.GetElementsByTagName("text");
        texts[0].AppendChild(xml.CreateTextNode(title));
        texts[1].AppendChild(xml.CreateTextNode(body));

        var toast = new ToastNotification(xml);
        // A reminder is useless once it's stale, so drop it from the notification center after its lifetime.
        toast.ExpiresAt = DateTimeOffset.Now + lifetime;
        ToastNotificationManager.CreateToastNotifier().Display(toast);
    }
}
