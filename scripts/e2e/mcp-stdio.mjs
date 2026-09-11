#!/usr/bin/env node
// Minimal MCP stdio client for the end-to-end test plan (docs/testing/e2e-test-plan.md).
// Spawns a server exe, performs the initialize handshake, sends each request in order and
// prints one JSON line per response (plus any notifications the server emits in between).
// It is a test harness only: it changes nothing on the machine beyond the child process it
// starts and stops.
//
//   node scripts/e2e/mcp-stdio.mjs --exe bundle/WindowsMcp.exe --list
//   node scripts/e2e/mcp-stdio.mjs --exe bundle/WindowsMcp.exe --call system_info '{"category":"os"}'
//   node scripts/e2e/mcp-stdio.mjs --exe bundle/WindowsMcp.exe --call screenshot '{}' -- --screenshot-scale 0.5
//   node scripts/e2e/mcp-stdio.mjs --exe bundle/WindowsMcp.exe --request '{"method":"tools/list","params":{}}' --out out.json
//
// Options: --timeout <ms> (per request, default 60000)  --env K=V (extra child env, repeatable)
//          --stderr (pass the server's stderr through)   --out <file> (write all responses as a JSON array)
//          --  (everything after it is passed to the server as its command line)
import { spawn } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';

const argv = process.argv.slice(2);
let exe = null, timeoutMs = 60000, requests = [], serverArgs = [], env = {}, passStderr = false, outFile = null;
for (let i = 0; i < argv.length; i++) {
  const a = argv[i];
  if (a === '--exe') exe = argv[++i];
  else if (a === '--timeout') timeoutMs = Number(argv[++i]);
  else if (a === '--request') requests.push(JSON.parse(argv[++i]));
  else if (a === '--request-file') requests.push(...JSON.parse(readFileSync(argv[++i], 'utf8')));
  else if (a === '--call') { const name = argv[++i]; const json = argv[++i] ?? '{}'; requests.push({ method: 'tools/call', params: { name, arguments: JSON.parse(json) } }); }
  else if (a === '--list') requests.push({ method: 'tools/list', params: {} });
  else if (a === '--env') { const m = /^([^=]+)=(.*)$/s.exec(argv[++i]); if (m) env[m[1]] = m[2]; }
  else if (a === '--stderr') passStderr = true;
  else if (a === '--out') outFile = argv[++i];
  else if (a === '--') { serverArgs = argv.slice(i + 1); break; }
  else { console.error(`unknown argument: ${a}`); process.exit(2); }
}
if (!exe) {
  console.error('usage: node mcp-stdio.mjs --exe <WindowsMcp.exe> [--list] [--call <tool> <json>]... [--request <json>]... [--timeout ms] [--env K=V] [--stderr] [--out file] [-- <server args>]');
  process.exit(2);
}

const child = spawn(exe, serverArgs, { env: { ...process.env, ...env }, stdio: ['pipe', 'pipe', passStderr ? 'inherit' : 'pipe'] });
const stderrLines = [];
if (!passStderr) child.stderr.setEncoding('utf8'), child.stderr.on('data', d => stderrLines.push(d));

const pending = new Map();
const responses = [];
let buffer = '';
child.stdout.setEncoding('utf8');
child.stdout.on('data', chunk => {
  buffer += chunk;
  let nl;
  while ((nl = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, nl).trim();
    buffer = buffer.slice(nl + 1);
    if (!line) continue;
    let msg;
    try { msg = JSON.parse(line); } catch { emit({ nonJsonStdout: line }); continue; }
    if (msg.id !== undefined && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
    else emit({ notification: msg });
  }
});
let exited = null;
child.on('exit', (code, signal) => { exited = { code, signal }; for (const [, resolve] of pending) resolve({ error: { message: `server exited (code ${code}, signal ${signal}) before answering`, stderr: stderrLines.join('') } }); pending.clear(); });
child.on('error', err => { console.error(`spawn failed: ${err.message}`); process.exit(3); });

function emit(obj) { responses.push(obj); console.log(JSON.stringify(obj)); }
let nextId = 1;
function send(method, params) {
  const id = nextId++;
  const line = JSON.stringify({ jsonrpc: '2.0', id, method, params });
  return new Promise(resolve => {
    const timer = setTimeout(() => { if (pending.has(id)) { pending.delete(id); resolve({ id, error: { message: `no response within ${timeoutMs} ms` }, timedOut: true }); } }, timeoutMs);
    pending.set(id, msg => { clearTimeout(timer); resolve(msg); });
    if (exited) { clearTimeout(timer); pending.delete(id); resolve({ id, error: { message: 'server already exited', stderr: stderrLines.join('') } }); return; }
    child.stdin.write(line + '\n');
  });
}

let failed = 0;
const init = await send('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'windows-mcp-e2e', version: '1.0' } });
emit({ request: 'initialize', ...init });
if (init.error) failed++;
else child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');

for (const r of requests) {
  const started = Date.now();
  const res = await send(r.method, r.params ?? {});
  emit({ request: r.method, tool: r.params?.name, elapsedMs: Date.now() - started, ...res });
  if (res.error || res.timedOut || res.result?.isError) failed++;
}

if (outFile) writeFileSync(outFile, JSON.stringify(responses, null, 2));
child.stdin.end();
setTimeout(() => { if (!exited) child.kill(); }, 1500);
child.on('exit', () => process.exit(failed ? 1 : 0));
