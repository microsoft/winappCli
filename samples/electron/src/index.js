const { app, BrowserWindow, ipcMain} = require('electron');
const path = require('node:path');

const addon = require('../addon/build/Release/addon.node');

let csAddon = undefined; 

function getCsAddon() {
  const csAddonPath = '../csAddon/dist/csAddon.node';
  if (csAddon === undefined) {
    csAddon = require(csAddonPath);
  }
  return csAddon;
}

// Handle creating/removing shortcuts on Windows when installing/uninstalling.
if (require('electron-squirrel-startup')) {
  app.quit();
}

const createWindow = () => {
  // Create the browser window.
  const mainWindow = new BrowserWindow({
    width: 800,
    height: 600,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
    },
  });

  // and load the index.html of the app.
  mainWindow.loadFile(path.join(__dirname, 'index.html'));
};

// This method will be called when Electron has finished
// initialization and is ready to create browser windows.
// Some APIs can only be used after this event occurs.
app.whenReady().then(() => {
  createWindow();

  // On OS X it's common to re-create a window in the app when the
  // dock icon is clicked and there are no other windows open.
  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      createWindow();
    }
  });
});

// Quit when all windows are closed, except on macOS. There, it's common
// for applications and their menu bar to stay active until the user quits
// explicitly with Cmd + Q.
app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') {
    app.quit();
  }
});

ipcMain.handle('show-notification', async (event, title, body) => {
  addon.showNotification(title, body);
});

ipcMain.handle('get-windows-app-runtime-version', async () => {
  return getCsAddon().Addon.getWindowsAppRuntimeVersion();
}); 
