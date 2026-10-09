use windows::core::Result;
use windows::ApplicationModel::Activation::AppActivationArguments;
use windows::UI::Notifications::{ToastNotification, ToastNotificationManager};
use windows_data_xml_dom::XmlDocument;

fn show_notification() -> Result<()> {
    let xml = XmlDocument::new()?;
    xml.LoadXml("<toast><visual><binding template=\"ToastGeneric\"><text>Tide alert</text></binding></visual></toast>")?;
    let notifier = ToastNotificationManager::CreateToastNotifierWithId("TideAlert.Desktop")?;
    notifier.Show(&ToastNotification::CreateToastNotification(&xml)?)?;
    Ok(())
}
