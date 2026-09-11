// Raw stdio driver: sends the given lines verbatim (no handshake, no parsing) and prints every stdout line with a timestamp.
// node scripts/e2e/raw.mjs --exe <exe> [--send <line>]... [--sleep <ms>]... [--wait <ms>] [--stderr] [-- <server args>]
import { spawn } from 'node:child_process';
const argv = process.argv.slice(2); let exe = null, steps = [], waitMs = 3000, serverArgs = [], passStderr = false;
for (let i = 0; i < argv.length; i++) { const a = argv[i];
  if (a === '--exe') exe = argv[++i]; else if (a === '--send') steps.push({ send: argv[++i] }); else if (a === '--sleep') steps.push({ sleep: +argv[++i] });
  else if (a === '--wait') waitMs = +argv[++i]; else if (a === '--stderr') passStderr = true; else if (a === '--') { serverArgs = argv.slice(i + 1); break; } }
const t0 = Date.now(); const child = spawn(exe, serverArgs, { stdio: ['pipe', 'pipe', 'pipe'] });
let buf = ''; child.stdout.on('data', d => { buf += d; let idx; while ((idx = buf.indexOf('\n')) >= 0) { const line = buf.slice(0, idx).replace(/\r$/, ''); buf = buf.slice(idx + 1); if (line.trim()) console.log(`[${Date.now() - t0}ms] ${line.slice(0, 700)}`); } });
child.stderr.on('data', d => { if (passStderr) for (const l of String(d).split('\n')) if (l.trim()) console.log(`[stderr ${Date.now() - t0}ms] ${l.slice(0, 300)}`); });
const sleep = ms => new Promise(r => setTimeout(r, ms));
for (const s of steps) { if (s.sleep) await sleep(s.sleep); else { console.log(`[${Date.now() - t0}ms] >> ${s.send.slice(0, 160)}`); child.stdin.write(s.send + '\n'); } }
await sleep(waitMs); child.stdin.end(); await sleep(800); if (child.exitCode === null) child.kill(); console.log(`[${Date.now() - t0}ms] exit=${child.exitCode}`);
