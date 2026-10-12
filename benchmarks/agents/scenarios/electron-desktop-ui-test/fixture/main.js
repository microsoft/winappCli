const { app, BrowserWindow, ipcMain } = require('electron');
const fs = require('fs');
const path = require('path');

function createWindow() {
  const win = new BrowserWindow({ width: 640, height: 360, title: 'Contoso Exporter', webPreferences: { preload: path.join(__dirname, 'preload.js') } });
  win.loadFile('index.html');
}

ipcMain.handle('export', async (_event, fileName) => {
  const rows = ['id,total', '1,42', '2,17'];
  fs.writeFileSync(path.join(app.getPath('documents'), fileName), rows.join('\n'));
  return 'Finished';
});

app.whenReady().then(createWindow);
