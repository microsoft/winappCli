// Registers as a Windows share target. Works only when the app runs with package identity.
pub fn register_share_target() -> windows::core::Result<()> {
    let _manager = windows::ApplicationModel::DataTransfer::DataTransferManager::GetForCurrentView()?;
    Ok(())
}
