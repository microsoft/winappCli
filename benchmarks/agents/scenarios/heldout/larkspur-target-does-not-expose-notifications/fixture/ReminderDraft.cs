using Windows.UI.Notifications;
namespace LarkspurLedger;
public static class ReminderDraft {
    public static ToastNotification Draft(Windows.Data.Xml.Dom.XmlDocument content)
        => new ToastNotification(content);
}
