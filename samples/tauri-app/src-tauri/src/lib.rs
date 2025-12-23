// Learn more about Tauri commands at https://tauri.app/develop/calling-rust/
#[tauri::command]
fn greet(name: &str) -> String {
    format!("Hello, {}! You've been greeted from Rust!", name)
}

#[tauri::command]
fn get_package_family_name() -> String {
    #[cfg(target_os = "windows")]
    {
        use windows::ApplicationModel::Package;
        // Attempt to get the current package. This fails if the app is not packaged (no identity).
        match Package::Current() {
            Ok(package) => {
                match package.Id() {
                    Ok(id) => {
                        match id.FamilyName() {
                            Ok(name) => name.to_string(),
                            Err(_) => "Error retrieving Family Name".to_string(),
                        }
                    }
                    Err(_) => "Error retrieving Package ID".to_string(),
                }
            }
            Err(_) => "No package identity".to_string(),
        }
    }
    #[cfg(not(target_os = "windows"))]
    {
        "Not running on Windows".to_string()
    }
}

#[tauri::command]
fn show_notification(title: &str, body: &str) -> Result<(), String> {
    #[cfg(target_os = "windows")]
    {
        use windows::UI::Notifications::{ToastNotificationManager, ToastTemplateType, ToastNotification};
        use windows::core::HSTRING;

        // Get the toast XML template
        let toast_xml = ToastNotificationManager::GetTemplateContent(ToastTemplateType::ToastText02)
            .map_err(|e| e.to_string())?;

        // Get the text nodes
        let text_nodes = toast_xml.GetElementsByTagName(&HSTRING::from("text"))
            .map_err(|e| e.to_string())?;

        // Set the title
        let title_node = toast_xml.CreateTextNode(&HSTRING::from(title))
            .map_err(|e| e.to_string())?;
        text_nodes.Item(0).map_err(|e| e.to_string())?
            .AppendChild(&title_node)
            .map_err(|e| e.to_string())?;

        // Set the body
        let body_node = toast_xml.CreateTextNode(&HSTRING::from(body))
            .map_err(|e| e.to_string())?;
        text_nodes.Item(1).map_err(|e| e.to_string())?
            .AppendChild(&body_node)
            .map_err(|e| e.to_string())?;

        // Create the notification
        let notification = ToastNotification::CreateToastNotification(&toast_xml)
            .map_err(|e| e.to_string())?;

        // Show the notification
        ToastNotificationManager::CreateToastNotifier()
            .map_err(|e| e.to_string())?
            .Show(&notification)
            .map_err(|e| e.to_string())?;

        Ok(())
    }
    #[cfg(not(target_os = "windows"))]
    {
        Err("This feature is only supported on Windows".to_string())
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .invoke_handler(tauri::generate_handler![greet, get_package_family_name, show_notification])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
