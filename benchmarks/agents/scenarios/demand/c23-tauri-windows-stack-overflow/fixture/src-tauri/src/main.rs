fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_process::init())
        .plugin(tauri_plugin_fs::init())
        .plugin(tauri_plugin_os::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_log::Builder::new().build())
        .invoke_handler(tauri::generate_handler![
            list_profiles,
            save_profile,
            export_report,
            import_report
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}

#[tauri::command]
fn list_profiles() -> Vec<String> {
    Vec::new()
}

#[tauri::command]
fn save_profile(_name: String) {}

#[tauri::command]
fn export_report(_path: String) {}

#[tauri::command]
fn import_report(_path: String) {}
