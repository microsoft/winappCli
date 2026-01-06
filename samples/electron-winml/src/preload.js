const { contextBridge, ipcRenderer } = require('electron');

// Expose IPC methods to the renderer process
contextBridge.exposeInMainWorld('electronAPI', {
  selectImage: () => ipcRenderer.invoke('select-image'),
  classifyImage: (imagePath) => ipcRenderer.invoke('classify-image', imagePath),
  generateText: (prompt) => ipcRenderer.invoke('generate-text', prompt),
});
