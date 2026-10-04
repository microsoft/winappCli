const { app } = require('electron');
const { io } = require('socket.io-client');
app.whenReady().then(() => {
  const socket = io('http://127.0.0.1:3000');
  socket.on('connect_error', (e) => console.error(e.message));
});
