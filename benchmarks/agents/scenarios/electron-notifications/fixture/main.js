const { app, BrowserWindow, Notification } = require('electron');
const path = require('path');

function runExport(win) {
  setTimeout(() => {
    const file = path.join(app.getPath('documents'), 'export.csv');
    new Notification({ title: 'Export finished', body: file }).show();
  }, 5000);
}

app.whenReady().then(() => {
  const win = new BrowserWindow({ width: 900, height: 600 });
  win.loadFile('index.html');
  runExport(win);
});
