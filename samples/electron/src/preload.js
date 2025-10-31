const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('electronAPI', {
  showNotification: (title, body) => ipcRenderer.invoke('show-notification', title, body),
  getWindowsAppRuntimeVersion: () => ipcRenderer.invoke('get-windows-app-runtime-version'),
});