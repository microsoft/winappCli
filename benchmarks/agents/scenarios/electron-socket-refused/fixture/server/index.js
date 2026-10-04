const { Server } = require('socket.io');
const port = process.env.PORT || 3001;
new Server(port).on('connection', (s) => s.emit('hello'));
console.log('listening on ' + port);
