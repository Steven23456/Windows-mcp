#!/usr/bin/env node
// Records test outcomes into docs/testing/e2e-test-results.md by test id, then recomputes the
// coverage table (via sync-results.mjs). Input: a JSON array of {id, status, evidence?, notes?}
// either as a file path or inline.
//
//   node scripts/e2e/record.mjs results.json
//   node scripts/e2e/record.mjs '[{"id":"XC-01","status":"PASS","evidence":"…"}]'
//
// Status must start with PASS, FAIL, BLOCKED, SKIPPED or NOT RUN. Pipes in evidence/notes are
// escaped. An unknown id is reported and skipped; nothing else in the document is touched.
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const resultsPath = join(root, 'docs', 'testing', 'e2e-test-results.md');
const arg = process.argv[2];
if (!arg) { console.error('usage: node record.mjs <results.json | inline JSON array>'); process.exit(2); }
const entries = JSON.parse(existsSync(arg) ? readFileSync(arg, 'utf8') : arg);
const ID = /^[A-Z][A-Z0-9_]*-\d{2}$/;
const cell = s => String(s ?? '').replace(/\r?\n/g, ' ').replace(/\|/g, '\\|').trim();

const text = readFileSync(resultsPath, 'utf8');
const eol = text.includes('\r\n') ? '\r\n' : '\n';
const lines = text.split(/\r?\n/);
const byId = new Map();
for (let i = 0; i < lines.length; i++) {
  const l = lines[i];
  if (!l.startsWith('|')) continue;
  const first = l.slice(1).split('|')[0].trim();
  if (ID.test(first)) byId.set(first, i);
}
let updated = 0; const unknown = [];
for (const e of entries) {
  if (!e || !ID.test(e.id ?? '')) { unknown.push(String(e?.id)); continue; }
  const i = byId.get(e.id);
  if (i === undefined) { unknown.push(e.id); continue; }
  const cells = lines[i].slice(1, -1).split(/(?<!\\)\|/).map(c => c.trim());
  const [id, tier, ch] = cells;
  const status = cell(e.status || 'NOT RUN');
  if (!/^(PASS|FAIL|BLOCKED|SKIPPED|NOT RUN)/.test(status)) { console.error(`bad status for ${id}: ${status}`); process.exit(2); }
  lines[i] = `| ${id} | ${tier} | ${ch} | ${status} | ${cell(e.evidence)} | ${cell(e.notes)} |`;
  updated++;
}
writeFileSync(resultsPath, lines.join(eol));
console.log(`updated ${updated} row(s)${unknown.length ? `; unknown ids: ${unknown.join(', ')}` : ''}`);
execFileSync(process.execPath, [join(root, 'scripts', 'e2e', 'sync-results.mjs')], { stdio: 'inherit' });
