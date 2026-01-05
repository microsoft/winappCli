use windows::ApplicationModel::Package;
use windows::UI::Notifications::{ToastNotificationManager, ToastTemplateType, ToastNotification};

fn show_notification(message: &str) -> windows::core::Result<()> {
    let toast_xml = ToastNotificationManager::GetTemplateContent(ToastTemplateType::ToastText01)?;
    let text_nodes = toast_xml.GetElementsByTagName(&windows::core::HSTRING::from("text"))?;
    text_nodes.Item(0)?.AppendChild(&toast_xml.CreateTextNode(&windows::core::HSTRING::from(message))?)?;

    let toast = ToastNotification::CreateToastNotification(&toast_xml)?;
    ToastNotificationManager::CreateToastNotifier()?.Show(&toast)?;
    Ok(())
}

fn main() {
    match Package::Current() {
        Ok(package) => {
            match package.Id() {
                Ok(id) => match id.FamilyName() {
                    Ok(name) => {
                        println!("Package Family Name: {}", name);
                        if let Err(e) = show_notification("hello from rust") {
                            println!("Error showing notification: {}", e);
                        }
                    },
                    Err(e) => println!("Error getting family name: {}", e),
                },
                Err(e) => println!("Error getting package ID: {}", e),
            }
        }
        Err(_) => println!("Not packaged"),
    }
}
