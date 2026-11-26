'use strict';

const net = require('node:net');
const { createHash } = require('node:crypto');

const prefaceSize = 37;
const ready = Buffer.from([1]);
const offline = Buffer.from([2]);
const duplicate = Buffer.from([3]);
const waiting = new Map();
const address = process.env.SCREENSHOW_RELAY_HOST || '0.0.0.0';
const port = Number(process.env.SCREENSHOW_RELAY_PORT || '47211');

if (!Number.isInteger(port) || port < 0 || port > 65535) {
    throw new Error('SCREENSHOW_RELAY_PORT must be between 0 and 65535.');
}

const server = net.createServer(socket => {
    socket.setNoDelay(true);
    socket.setKeepAlive(true, 10000);
    socket.setTimeout(8000, () => socket.destroy());
    socket.on('error', () => socket.destroy());

    socket.on('readable', function readPreface() {
        const packet = socket.read(prefaceSize);
        if (!packet) return;
        socket.removeListener('readable', readPreface);

        const role = packet.toString('ascii', 0, 5);
        const key = packet.subarray(5);
        if (role === 'SSR1H') {
            register(socket, createHash('sha256').update(key).digest('hex'));
        } else if (role === 'SSR1V') {
            connect(socket, key.toString('hex'));
        } else {
            socket.destroy();
        }
    });
});

function register(socket, route) {
    if (waiting.has(route)) {
        socket.end(duplicate);
        return;
    }
    waiting.set(route, socket);
    socket.once('close', () => {
        if (waiting.get(route) === socket) waiting.delete(route);
    });
    socket.setTimeout(0);
    socket.write(ready);
}

function connect(socket, route) {
    const host = waiting.get(route);
    if (!host || host.destroyed || host.readableEnded) {
        socket.end(offline);
        return;
    }
    waiting.delete(route);
    socket.setTimeout(0);
    host.write(ready);
    socket.write(ready);

    host.once('error', () => socket.destroy());
    socket.once('error', () => host.destroy());
    host.once('close', () => { if (!host.readableEnded) socket.destroy(); });
    socket.once('close', () => { if (!socket.readableEnded) host.destroy(); });
    host.pipe(socket);
    socket.pipe(host);
}

server.maxConnections = 512;
server.on('error', error => {
    process.stderr.write(error.message + '\n');
    process.exitCode = 1;
});
server.listen(port, address, () => {
    const bound = server.address();
    process.stdout.write('ScreenShow relay listening on ' + bound.address + ':' + bound.port + '\n');
});
