const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('exporter', { run: (name) => ipcRenderer.invoke('export', name) });
