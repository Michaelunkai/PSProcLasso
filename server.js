/*
 * PSProcLasso live dashboard server.
 *
 * Zero dependencies (Node built-ins only).  In the background it periodically
 * runs `powershell -File PSProcLasso.ps1 -Snapshot`, which reuses the app's own
 * sampling code, and serves the cached snapshot instantly to the browser.
 *
 *   node server.js [--port N]
 *
 * Routes:
 *   /                -> index.html (the dashboard)
 *   /api/snapshot    -> { ok, stale, updatedAt, data }  (cached JSON snapshot)
 */
'use strict';

const http = require('http');
const { execFile } = require('child_process');
const fs = require('fs');
const path = require('path');

const PORT = parseInt(process.argv[process.argv.indexOf('--port') + 1] || '4173', 10);
// Sampling is heavy (the machine may be loaded), so refresh slowly and let
// the overlap guard in refreshSnapshot() skip cycles that are still running.
const REFRESH_MS = 8000;
const ROOT = __dirname;
const PS1 = path.join(ROOT, 'PSProcLasso.ps1');
const POWERSHELL = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');

let cache = {
  ok: false,
  stale: true,
  updatedAt: null,
  data: null,
};
let running = false;
let lastError = null;

function refreshSnapshot() {
  if (running) return; // never overlap spawns
  running = true;
  execFile(
    POWERSHELL,
    ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', PS1, '-Snapshot'],
    { timeout: 60000, windowsHide: true, maxBuffer: 64 * 1024 * 1024 },
    (err, stdout) => {
      running = false;
      if (err) {
        lastError = String(err.message || err);
        return; // keep serving the last good snapshot; it will go stale
      }
      try {
        const data = JSON.parse(stdout);
        cache = { ok: true, stale: false, updatedAt: new Date().toISOString(), data };
        lastError = null;
        console.log('[psproclasso] snapshot updated: ' + data.processes + ' processes');
      } catch (e) {
        lastError = 'JSON parse: ' + e.message;
      }
    }
  );
}

function serveFile(res, file, type) {
  fs.readFile(file, (err, buf) => {
    if (err) {
      res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
      res.end('not found: ' + file);
      return;
    }
    res.writeHead(200, { 'Content-Type': type + '; charset=utf-8' });
    res.end(buf);
  });
}

const server = http.createServer((req, res) => {
  const url = req.url.split('?')[0];
  if (url === '/' || url === '/index.html') {
    serveFile(res, path.join(ROOT, 'index.html'), 'text/html');
  } else if (url === '/api/snapshot') {
    res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify({ ...cache, lastError }));
  } else {
    res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
    res.end('404');
  }
});

server.on('error', (e) => {
  console.error('[psproclasso] server error: ' + e.message);
  if (e.code === 'EADDRINUSE') process.exit(2);
});

server.listen(PORT, '127.0.0.1', () => {
  console.log('[psproclasso] dashboard listening on http://127.0.0.1:' + PORT);
});

refreshSnapshot();
setInterval(refreshSnapshot, REFRESH_MS);
